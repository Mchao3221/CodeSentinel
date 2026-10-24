using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 审计覆盖验收测试：验证各业务动作都写入正确 action、target 与 actor 的审计行。
/// </summary>
public class AuditCoverageTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";
    private const string CommentPath = "a.txt";

    /// <summary>
    /// 用户管理动作：创建、更新与重置密码都应写入审计，操作人为管理员，目标为用户。
    /// </summary>
    [Fact]
    public async Task User_management_writes_audit_rows()
    {
        using var factory = new TestAppFactory();
        var admin = await factory.LoginAsAdminAsync();

        var created = await admin.PostJsonWithCsrfAsync(
            "/api/users",
            new { username = "audited-user", password = "Passw0rd!", displayName = "被审计用户" });
        var createdBody = (await created.Content.ReadFromJsonAsync<UserBody>())!;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var patched = await admin.PatchJsonWithCsrfAsync(
            $"/api/users/{createdBody.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var reset = await admin.PostJsonWithCsrfAsync(
            $"/api/users/{createdBody.Id}/reset-password", new { });
        var resetBody = (await reset.Content.ReadFromJsonAsync<ResetPasswordBody>())!;
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var createdLog = await SingleAuditAsync(db, "USER_CREATED", createdBody.Id);
            Assert.Equal(factory.AdminUserId, createdLog.ActorId);
            Assert.Equal("USER", createdLog.TargetType);

            var updatedLog = await SingleAuditAsync(db, "USER_UPDATED", createdBody.Id);
            Assert.Equal(factory.AdminUserId, updatedLog.ActorId);
            Assert.Contains("isActive", updatedLog.DetailJson, StringComparison.Ordinal);

            var resetLog = await SingleAuditAsync(db, "PASSWORD_RESET", createdBody.Id);
            Assert.Equal(factory.AdminUserId, resetLog.ActorId);
            // 重置密码审计绝不能泄露新密码
            Assert.DoesNotContain(resetBody.Password, resetLog.DetailJson ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 用户修改自己的密码应写入 PASSWORD_CHANGED，操作人为用户本人。
    /// </summary>
    [Fact]
    public async Task Password_change_writes_self_audit_row()
    {
        using var factory = new TestAppFactory();
        var user = factory.SeedUser("self-changer", "OldPass123!");
        var client = await factory.LoginAsync(user.Username, "OldPass123!");

        var change = await client.PostJsonWithCsrfAsync(
            "/api/auth/change-password",
            new { currentPassword = "OldPass123!", newPassword = "NewPass123!" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await SingleAuditAsync(db, "PASSWORD_CHANGED", user.Id);
        Assert.Equal(user.Id, log.ActorId);
        Assert.Equal("USER", log.TargetType);
    }

    /// <summary>
    /// 登录与仓库管理动作都应写入审计：LOGIN、REPO_CREATED、REPO_UPDATED、REPO_MEMBERS_UPDATED。
    /// </summary>
    [Fact]
    public async Task Login_and_repo_writes_audit_rows()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var admin = await factory.LoginAsAdminAsync();
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);

        var repo = await CreateRepoAsync(admin, UniqueName("audited-repo"), source.Url);

        var patched = await admin.PatchJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}", new { syncIntervalSeconds = 120 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var put = await admin.PutJsonWithCsrfAsync(
            $"/api/repos/{repo.Id}/members",
            new { members = new[] { new { userId = merger.Id, isMerger = true } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var loginLog = await SingleAuditAsync(db, "LOGIN", factory.AdminUserId);
            Assert.Equal("USER", loginLog.TargetType);

            var createdLog = await SingleAuditAsync(db, "REPO_CREATED", repo.Id);
            Assert.Equal("REPO", createdLog.TargetType);

            var updatedLog = await SingleAuditAsync(db, "REPO_UPDATED", repo.Id);
            Assert.Equal("REPO", updatedLog.TargetType);

            var membersLog = await SingleAuditAsync(db, "REPO_MEMBERS_UPDATED", repo.Id);
            Assert.Equal("REPO", membersLog.TargetType);

            Assert.Equal(factory.AdminUserId, loginLog.ActorId);
            Assert.Equal(factory.AdminUserId, createdLog.ActorId);
            Assert.Equal(factory.AdminUserId, updatedLog.ActorId);
            Assert.Equal(factory.AdminUserId, membersLog.ActorId);
        }
    }

    /// <summary>
    /// 评审生命周期：创建、结论、评论创建、评论删除与手工标记合并都应写入审计。
    /// </summary>
    [Fact]
    public async Task Review_decide_comment_and_merge_write_audit_rows()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var review = await CreateReviewAsync(submitter, context);

        var comment = await CreateCommentAsync(submitter, review.Id, "这是评论");
        var commentBody = (await comment.Content.ReadFromJsonAsync<CommentBody>())!;
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);

        var deleted = await submitter.DeleteWithCsrfAsync($"/api/comments/{commentBody.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var decided = await reviewer.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/decisions", new { state = "APPROVED", note = (string?)null });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        var merged = await admin.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var createdLog = await SingleAuditAsync(db, "REVIEW_CREATED", review.Id);
        Assert.Equal(context.Submitter.Id, createdLog.ActorId);
        Assert.Equal("REVIEW", createdLog.TargetType);

        var decidedLog = await SingleAuditAsync(db, "REVIEW_DECIDED", review.Id);
        Assert.Equal(context.Reviewer.Id, decidedLog.ActorId);

        var commentCreated = await SingleAuditAsync(db, "COMMENT_CREATED", commentBody.Id);
        Assert.Equal(context.Submitter.Id, commentCreated.ActorId);
        Assert.Equal("COMMENT", commentCreated.TargetType);

        var commentDeleted = await SingleAuditAsync(db, "COMMENT_DELETED", commentBody.Id);
        Assert.Equal(context.Submitter.Id, commentDeleted.ActorId);

        var mergedLog = await SingleAuditAsync(db, "REVIEW_MERGED", review.Id);
        Assert.Equal(factory.AdminUserId, mergedLog.ActorId);
    }

    /// <summary>
    /// 关闭评审单应写入 REVIEW_CLOSED，操作人为提交人。
    /// </summary>
    [Fact]
    public async Task Review_close_writes_audit_row()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);

        var closed = await submitter.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/close", new { reason = "不再需要" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await SingleAuditAsync(db, "REVIEW_CLOSED", review.Id);
        Assert.Equal(context.Submitter.Id, log.ActorId);
        Assert.Equal("REVIEW", log.TargetType);
    }

    /// <summary>
    /// 新版本生成应写入 REVISION_CREATED，操作人为系统（空）。
    /// </summary>
    [Fact]
    public async Task Revision_creation_writes_audit_row()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RevisionService>();
            Assert.True(await service.AddRevisionAsync(review.Id, newHead, CancellationToken.None));
        }

        using (var verify = factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await SingleAuditAsync(db, "REVISION_CREATED", review.Id);
            Assert.Null(log.ActorId);
            Assert.Equal("REVIEW", log.TargetType);
        }
    }

    /// <summary>
    /// 读取并断言唯一一条指定动作的审计行。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="action">动作标识</param>
    /// <param name="targetId">目标主键</param>
    /// <returns>审计行</returns>
    private static async Task<AuditLog> SingleAuditAsync(
        AppDbContext db,
        string action,
        Guid targetId)
    {
        return await db.AuditLogs.AsNoTracking()
            .SingleAsync(m => m.Action == action && m.TargetId == targetId);
    }

    /// <summary>
    /// 准备仓库、提交人、审核人、合并人与带差异内容的 feature 分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>审计测试上下文</returns>
    private static async Task<AuditContext> SeedContextAsync(TestAppFactory factory, TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("audit"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewer.Id, isMerger: false);

        await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { [CommentPath] = Text("line1\nline2\nline3\n") },
            message: "base");
        await source.CreateBranchFromMainAsync(HeadBranch);
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { [CommentPath] = Text("line0\nline1\nline3\nline4\n") });
        await FetchMirrorAsync(factory, repo);

        return new AuditContext(source, repo, submitter, reviewer);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="isMerger">是否合并人</param>
    private static void SeedMember(TestAppFactory factory, Guid repoId, Guid userId, bool isMerger)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RepoMembers.Add(new RepoMember
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            UserId = userId,
            IsMerger = isMerger
        });
        db.SaveChanges();
    }

    /// <summary>
    /// 用真实 git 把远端分支拉进本地镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repo">仓库</param>
    /// <returns></returns>
    private static async Task FetchMirrorAsync(TestAppFactory factory, Repo repo)
    {
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        await mirror.FetchAsync(repo.Id, repo.GitUrl, null, CancellationToken.None);
    }

    /// <summary>
    /// 通过 HTTP 创建评审单并返回详情。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="context">审计测试上下文</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        AuditContext context)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId = context.Repo.Id,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title = UniqueName("审计测试"),
                description = (string?)null,
                reviewerIds = new[] { context.Reviewer.Id }
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 创建一条 NEW 侧行评论。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="body">评论内容</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> CreateCommentAsync(
        HttpClient client,
        Guid reviewId,
        string body)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/comments",
            new { path = CommentPath, side = "NEW", line = 1, body, parentId = (Guid?)null });
    }

    /// <summary>
    /// 以管理员身份创建仓库，返回响应体。
    /// </summary>
    /// <param name="admin">管理员客户端</param>
    /// <param name="name">仓库名称</param>
    /// <param name="gitUrl">仓库地址</param>
    /// <returns>仓库响应</returns>
    private static async Task<RepoBody> CreateRepoAsync(
        HttpClient admin,
        string name,
        string gitUrl)
    {
        var response = await admin.PostJsonWithCsrfAsync(
            "/api/repos",
            new
            {
                name,
                gitUrl,
                defaultBaseBranch = "main",
                credential = (string?)null,
                credentialKind = (string?)null
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RepoBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 把 UTF-8 文本转成字节数组。
    /// </summary>
    /// <param name="value">文本</param>
    /// <returns>字节内容</returns>
    private static byte[] Text(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    /// <summary>
    /// 生成不重复的用户名或仓库名。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 审计测试上下文。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    private sealed record AuditContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer);
}
