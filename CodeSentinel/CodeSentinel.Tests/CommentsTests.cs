using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 行级评论集成测试：在真实 SQLite 与真实 git 仓库上走 HTTP 验证创建、回复、编辑、删除、解决与列表。
/// </summary>
public class CommentsTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";
    private const string CommentPath = "a.txt";

    /// <summary>
    /// 位于基线的三行内容。
    /// </summary>
    private const string BaseContent = "line1\nline2\nline3\n";

    /// <summary>
    /// 位于评审分支的内容：新增 line0、删除 line2、新增 line4。
    /// </summary>
    private const string HeadContent = "line0\nline1\nline3\nline4\n";

    /// <summary>
    /// NEW 侧的新增行与 OLD 侧的删除行都能评论，并写入原文与 blob sha。
    /// </summary>
    [Fact]
    public async Task Can_comment_on_add_and_delete_lines()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        var added = await CreateCommentAsync(client, review.Id, CommentPath, "NEW", 1, "这是新增行");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var addedBody = await added.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(addedBody);
        Assert.Equal("NEW", addedBody.Side);
        Assert.Equal(1, addedBody.Line);
        Assert.Equal("OPEN", addedBody.Status);
        Assert.Equal(context.Submitter.Id, addedBody.AuthorId);
        Assert.Equal(context.Submitter.DisplayName, addedBody.AuthorDisplayName);
        Assert.Null(addedBody.ParentId);

        var contextLine = await CreateCommentAsync(client, review.Id, CommentPath, "NEW", 2, "这是上下文行");
        Assert.Equal(HttpStatusCode.Created, contextLine.StatusCode);

        var deleted = await CreateCommentAsync(client, review.Id, CommentPath, "OLD", 2, "这是删除行");
        Assert.Equal(HttpStatusCode.Created, deleted.StatusCode);
        var deletedBody = await deleted.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(deletedBody);
        Assert.Equal("OLD", deletedBody.Side);
        Assert.Equal(2, deletedBody.Line);

        var newBlobSha = await RevParseBlobAsync(source, context.HeadSha, CommentPath);
        var oldBlobSha = await RevParseBlobAsync(source, context.BaseSha, CommentPath);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedAdded = await db.Comments.AsNoTracking().SingleAsync(m => m.Id == addedBody.Id);
        Assert.Equal("line0", storedAdded.AnchorText);
        Assert.Equal(newBlobSha, storedAdded.BlobSha);
        Assert.Equal(review.Revisions[0].Id, storedAdded.RevisionId);

        var storedDeleted = await db.Comments.AsNoTracking().SingleAsync(m => m.Id == deletedBody.Id);
        Assert.Equal("line2", storedDeleted.AnchorText);
        Assert.Equal(oldBlobSha, storedDeleted.BlobSha);

        Assert.Single(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == addedBody.Id && m.Action == "COMMENT_CREATED")
                .ToListAsync());
    }

    /// <summary>
    /// 行号不在当前版本 diff 中时返回 400 LINE_NOT_IN_DIFF。
    /// </summary>
    [Fact]
    public async Task Rejects_line_not_present_in_diff()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        var response = await CreateCommentAsync(client, review.Id, CommentPath, "NEW", 99, "越界行");

        var error = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("LINE_NOT_IN_DIFF", error.Code);
    }

    /// <summary>
    /// 回复必须指向同一评审单中未删除的评论，并通知被回复人；回复自己不发通知。
    /// </summary>
    [Fact]
    public async Task Reply_requires_same_review_and_sets_parent()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);
        var parent = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 1, "父评论");
        var parentBody = await parent.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(parentBody);

        var reply = await CreateCommentAsync(
            reviewer, review.Id, CommentPath, "NEW", 1, "第一行回复", parentBody.Id);

        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        var replyBody = await reply.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(replyBody);
        Assert.Equal(parentBody.Id, replyBody.ParentId);
        Assert.Equal(context.Reviewer.Id, replyBody.AuthorId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notifications = await db.Notifications.AsNoTracking()
                .Where(m => m.Type == NotificationType.CommentReply && m.ReviewId == review.Id)
                .ToListAsync();
            var notification = Assert.Single(notifications);
            Assert.Equal(context.Submitter.Id, notification.UserId);

            using var payload = JsonDocument.Parse(notification.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(review.Id, root.GetProperty("reviewId").GetGuid());
            Assert.Equal(CommentPath, root.GetProperty("path").GetString());
            Assert.Equal(1, root.GetProperty("line").GetInt32());
            Assert.Contains("第一行回复", root.GetProperty("bodyExcerpt").GetString());
            Assert.Equal(context.Reviewer.DisplayName, root.GetProperty("authorDisplayName").GetString());

            // 跨评审单的父评论不允许回复
            var foreignParent = new Comment
            {
                Id = Guid.NewGuid(),
                ReviewId = Guid.NewGuid(),
                RevisionId = review.Revisions[0].Id,
                AuthorId = context.Reviewer.Id,
                FilePath = CommentPath,
                Side = "NEW",
                LineNumber = 1,
                AnchorText = "line0",
                BlobSha = new string('a', 40),
                Body = "外部评论",
                Status = CommentStatus.Open
            };
            db.Comments.Add(foreignParent);
            await db.SaveChangesAsync();
        }

        var foreignParentId = await ReadLatestForeignCommentIdAsync(factory, review.Id);
        var crossReview = await CreateCommentAsync(
            reviewer, review.Id, CommentPath, "NEW", 1, "跨单回复", foreignParentId);
        Assert.Equal(HttpStatusCode.BadRequest, crossReview.StatusCode);
        Assert.Equal(
            "INVALID_PARENT",
            (await crossReview.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        // 回复自己没有通知
        var selfReply = await CreateCommentAsync(
            submitter, review.Id, CommentPath, "NEW", 1, "自己回复自己", parentBody.Id);
        Assert.Equal(HttpStatusCode.Created, selfReply.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                1,
                await db.Notifications.AsNoTracking()
                    .CountAsync(m => m.Type == NotificationType.CommentReply && m.ReviewId == review.Id));
        }

        // 已删除的父评论不允许回复
        var deletedParent = await submitter.DeleteWithCsrfAsync($"/api/comments/{parentBody.Id}");
        Assert.Equal(HttpStatusCode.OK, deletedParent.StatusCode);
        var replyToDeleted = await CreateCommentAsync(
            reviewer, review.Id, CommentPath, "NEW", 1, "回复已删除", parentBody.Id);
        Assert.Equal(HttpStatusCode.BadRequest, replyToDeleted.StatusCode);
        Assert.Equal(
            "INVALID_PARENT",
            (await replyToDeleted.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 只有作者能编辑评论；作者或管理员能删除；已删除的评论不可编辑。
    /// </summary>
    [Fact]
    public async Task Only_author_can_edit_and_delete_comment()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);
        var created = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 1, "原始内容");
        var comment = await created.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(comment);

        var deniedEdit = await reviewer.PatchJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}", new { body = "越权编辑" });
        Assert.Equal(HttpStatusCode.Forbidden, deniedEdit.StatusCode);
        Assert.Equal("FORBIDDEN", (await deniedEdit.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var deniedDelete = await reviewer.DeleteWithCsrfAsync($"/api/comments/{comment.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);
        Assert.Equal("FORBIDDEN", (await deniedDelete.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        // 非仓库成员按评论不存在处理
        var hidden = await outsiderClient.PatchJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}", new { body = "外部成员编辑" });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var edited = await submitter.PatchJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}", new { body = "  修改后的内容  " });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editedBody = await edited.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(editedBody);
        Assert.Equal("修改后的内容", editedBody.Body);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == comment.Id && m.Action == "COMMENT_EDITED")
                .ToListAsync());
        }

        // 已解决的评论仍可编辑
        var resolved = await submitter.PostJsonWithCsrfAsync($"/api/comments/{comment.Id}/resolve", new { });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var editedResolved = await submitter.PatchJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}", new { body = "解决后编辑" });
        Assert.Equal(HttpStatusCode.OK, editedResolved.StatusCode);

        // 管理员可以删除他人的评论
        var admin = await factory.LoginAsAdminAsync();
        var adminDelete = await admin.DeleteWithCsrfAsync($"/api/comments/{comment.Id}");
        Assert.Equal(HttpStatusCode.OK, adminDelete.StatusCode);
        Assert.Equal("DELETED", (await adminDelete.Content.ReadFromJsonAsync<CommentBody>())!.Status);

        // 已删除的评论不可再编辑
        var editDeleted = await submitter.PatchJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}", new { body = "删除后编辑" });
        Assert.Equal(HttpStatusCode.Conflict, editDeleted.StatusCode);
        Assert.Equal(
            "COMMENT_NOT_EDITABLE",
            (await editDeleted.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 删除是软删除：记录保留、写审计、更新状态，回复不被级联删除。
    /// </summary>
    [Fact]
    public async Task Deleted_comment_is_soft_deleted_and_audited()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);
        var parent = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 1, "会被删除");
        var parentBody = await parent.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(parentBody);
        var reply = await CreateCommentAsync(
            reviewer, review.Id, CommentPath, "NEW", 1, "回复保留", parentBody.Id);
        var replyBody = await reply.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(replyBody);

        var deleted = await submitter.DeleteWithCsrfAsync($"/api/comments/{parentBody.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedParent = await db.Comments.AsNoTracking().SingleAsync(m => m.Id == parentBody.Id);
        Assert.Equal(CommentStatus.Deleted, storedParent.Status);
        Assert.Equal("会被删除", storedParent.Body);

        var storedReply = await db.Comments.AsNoTracking().SingleAsync(m => m.Id == replyBody.Id);
        Assert.Equal(CommentStatus.Open, storedReply.Status);

        var audit = Assert.Single(await db.AuditLogs.AsNoTracking()
            .Where(m => m.TargetId == parentBody.Id && m.Action == "COMMENT_DELETED")
            .ToListAsync());
        Assert.Equal(context.Submitter.Id, audit.ActorId);
    }

    /// <summary>
    /// 作者或仓库合并人可以解决与取消解决，其余成员返回 403，非法状态返回 409。
    /// </summary>
    [Fact]
    public async Task Author_or_merger_can_resolve_and_unresolve()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var merger = await factory.LoginAsync(context.Merger.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);
        var created = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 1, "待解决");
        var comment = await created.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(comment);

        var denied = await reviewer.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/resolve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var resolved = await submitter.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/resolve", new { });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolvedBody = await resolved.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(resolvedBody);
        Assert.Equal("RESOLVED", resolvedBody.Status);
        Assert.Equal(context.Submitter.Id, resolvedBody.ResolvedBy);
        Assert.NotNull(resolvedBody.ResolvedAt);

        var unresolved = await submitter.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/unresolve", new { });
        Assert.Equal(HttpStatusCode.OK, unresolved.StatusCode);
        var unresolvedBody = await unresolved.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(unresolvedBody);
        Assert.Equal("OPEN", unresolvedBody.Status);
        Assert.Null(unresolvedBody.ResolvedBy);
        Assert.Null(unresolvedBody.ResolvedAt);

        // 合并人可以解决他人的评论
        var mergerResolved = await merger.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/resolve", new { });
        Assert.Equal(HttpStatusCode.OK, mergerResolved.StatusCode);
        var mergerBody = await mergerResolved.Content.ReadFromJsonAsync<CommentBody>();
        Assert.NotNull(mergerBody);
        Assert.Equal("RESOLVED", mergerBody.Status);
        Assert.Equal(context.Merger.Id, mergerBody.ResolvedBy);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                2,
                await db.AuditLogs.AsNoTracking()
                    .CountAsync(m => m.TargetId == comment.Id && m.Action == "COMMENT_RESOLVED"));
            Assert.Single(await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == comment.Id && m.Action == "COMMENT_UNRESOLVED")
                .ToListAsync());
        }

        // 已取消解决的评论再次取消解决返回 409，已删除的评论解决也返回 409
        var notResolved = await merger.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/unresolve", new { });
        Assert.Equal(HttpStatusCode.OK, notResolved.StatusCode);
        var unresolveOpen = await merger.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/unresolve", new { });
        Assert.Equal(HttpStatusCode.Conflict, unresolveOpen.StatusCode);
        Assert.Equal(
            "COMMENT_NOT_RESOLVABLE",
            (await unresolveOpen.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var deleted = await submitter.DeleteWithCsrfAsync($"/api/comments/{comment.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var resolveDeleted = await submitter.PostJsonWithCsrfAsync(
            $"/api/comments/{comment.Id}/resolve", new { });
        Assert.Equal(HttpStatusCode.Conflict, resolveDeleted.StatusCode);
        Assert.Equal(
            "COMMENT_NOT_RESOLVABLE",
            (await resolveDeleted.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 超大文件与二进制文件不允许行级评论，返回 400 FILE_NOT_COMMENTABLE。
    /// </summary>
    [Fact]
    public async Task Comment_on_too_large_file_returns_400_FILE_NOT_COMMENTABLE()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var largeText = string.Join('\n', Enumerable.Range(0, 100_000).Select(m => $"line {m} padding padding padding"));
        await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]>
            {
                ["large.txt"] = Text(largeText),
                ["bin.dat"] = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x10]
            });
        await FetchMirrorAsync(factory, context.Repo);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(client, context);

        var tooLarge = await CreateCommentAsync(client, review.Id, "large.txt", "NEW", 1, "超大文件");
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(
            "FILE_NOT_COMMENTABLE",
            (await tooLarge.Content.ReadFromJsonAsync<ErrorBody>())!.Code);

        var binary = await CreateCommentAsync(client, review.Id, "bin.dat", "NEW", 1, "二进制文件");
        Assert.Equal(HttpStatusCode.BadRequest, binary.StatusCode);
        Assert.Equal(
            "FILE_NOT_COMMENTABLE",
            (await binary.Content.ReadFromJsonAsync<ErrorBody>())!.Code);
    }

    /// <summary>
    /// 列表接口返回全部状态（含 DELETED 与 OUTDATED）并按创建时间升序，非成员按不存在处理。
    /// </summary>
    [Fact]
    public async Task List_returns_all_statuses_in_created_order_and_requires_membership()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context);

        var first = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 1, "第一条");
        var second = await CreateCommentAsync(reviewer, review.Id, CommentPath, "NEW", 2, "第二条");
        var third = await CreateCommentAsync(submitter, review.Id, CommentPath, "NEW", 3, "第三条");
        var firstBody = (await first.Content.ReadFromJsonAsync<CommentBody>())!;
        var secondBody = (await second.Content.ReadFromJsonAsync<CommentBody>())!;
        var thirdBody = (await third.Content.ReadFromJsonAsync<CommentBody>())!;

        var resolvedThird = await submitter.DeleteWithCsrfAsync($"/api/comments/{thirdBody.Id}");
        Assert.Equal(HttpStatusCode.OK, resolvedThird.StatusCode);

        // 固定创建时间，避免同一毫秒内的顺序依赖
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var baseTime = DateTime.UtcNow.AddMinutes(-10);
            await db.Comments.Where(m => m.Id == firstBody.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(c => c.CreatedAt, baseTime));
            await db.Comments.Where(m => m.Id == secondBody.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(c => c.CreatedAt, baseTime.AddMinutes(1)));
            await db.Comments.Where(m => m.Id == thirdBody.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(c => c.CreatedAt, baseTime.AddMinutes(2)));
            await db.Comments.Where(m => m.Id == secondBody.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(c => c.Status, CommentStatus.Outdated));
        }

        var list = await reviewer.GetFromJsonAsync<CommentListBody>($"/api/reviews/{review.Id}/comments");
        Assert.NotNull(list);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(
            new[] { firstBody.Id, secondBody.Id, thirdBody.Id },
            list.Items.Select(m => m.Id).ToArray());
        Assert.Equal(new[] { "OPEN", "OUTDATED", "DELETED" }, list.Items.Select(m => m.Status).ToArray());
        Assert.Equal("第三条", list.Items[2].Body);
        Assert.Equal(context.Reviewer.DisplayName, list.Items[1].AuthorDisplayName);

        var denied = await outsiderClient.GetAsync($"/api/reviews/{review.Id}/comments");
        var error = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
    }

    /// <summary>
    /// 准备仓库、提交人、审核人、合并人与 feature 分支上的差异内容。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>评论测试上下文</returns>
    private static async Task<CommentContext> SeedContextAsync(TestAppFactory factory, TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("comments"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewer.Id, isMerger: false);
        SeedMember(factory, repo.Id, merger.Id, isMerger: true);

        var baseSha = await source.CommitChangesAsync(
            BaseBranch,
            new Dictionary<string, byte[]> { [CommentPath] = Text(BaseContent) },
            message: "base content");
        await source.CreateBranchFromMainAsync(HeadBranch);
        var headSha = await source.CommitChangesAsync(
            HeadBranch,
            new Dictionary<string, byte[]> { [CommentPath] = Text(HeadContent) });
        await FetchMirrorAsync(factory, repo);

        return new CommentContext(source, repo, submitter, reviewer, merger, baseSha, headSha);
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
    /// <param name="context">评论测试上下文</param>
    /// <param name="headSha">期望的评审提交，用于断言</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        CommentContext context,
        string? headSha = null)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId = context.Repo.Id,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title = UniqueName("评论测试"),
                description = (string?)null,
                reviewerIds = new[] { context.Reviewer.Id }
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);

        if (headSha is not null)
        {
            Assert.Equal(headSha, body.HeadSha);
        }

        return body;
    }

    /// <summary>
    /// 调用创建评论接口。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="side">OLD 或 NEW</param>
    /// <param name="line">行号</param>
    /// <param name="body">评论内容</param>
    /// <param name="parentId">回复的父评论主键</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> CreateCommentAsync(
        HttpClient client,
        Guid reviewId,
        string path,
        string side,
        int line,
        string body,
        Guid? parentId = null)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/comments",
            new { path, side, line, body, parentId });
    }

    /// <summary>
    /// 读取刚写入的跨评审单父评论主键。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">当前评审单主键</param>
    /// <returns>跨评审单评论主键</returns>
    private static async Task<Guid> ReadLatestForeignCommentIdAsync(
        TestAppFactory factory,
        Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Comments.AsNoTracking()
            .Where(m => m.ReviewId != reviewId)
            .Select(m => m.Id)
            .FirstAsync();
    }

    /// <summary>
    /// 在裸仓库中读取指定提交下文件内容的 blob id，用于独立校验服务写入的 blob_sha。
    /// </summary>
    /// <param name="source">真实 git 测试仓库</param>
    /// <param name="commitSha">提交 SHA</param>
    /// <param name="path">文件路径</param>
    /// <returns>blob id</returns>
    private static async Task<string> RevParseBlobAsync(
        TestGitRepo source,
        string commitSha,
        string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = source.BarePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--verify");
        startInfo.ArgumentList.Add($"{commitSha}:{path}");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git rev-parse {commitSha}:{path} 执行失败（{process.ExitCode}）：{stdErr}");
        }

        return stdOut.Trim();
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
    /// 评论测试上下文：真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    /// <param name="Merger">仓库合并人</param>
    /// <param name="BaseSha">基线提交</param>
    /// <param name="HeadSha">评审分支提交</param>
    private sealed record CommentContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer,
        User Merger,
        string BaseSha,
        string HeadSha);
}

/// <summary>
/// 评论响应 DTO。
/// </summary>
/// <param name="Id">评论主键</param>
/// <param name="RevisionId">所属评审版本主键</param>
/// <param name="AuthorId">作者主键</param>
/// <param name="AuthorDisplayName">作者显示名</param>
/// <param name="Path">文件路径</param>
/// <param name="Side">OLD 或 NEW</param>
/// <param name="Line">该侧行号</param>
/// <param name="Body">评论内容</param>
/// <param name="ParentId">父评论主键</param>
/// <param name="Status">评论状态</param>
/// <param name="ResolvedBy">解决人</param>
/// <param name="ResolvedAt">解决时间</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record CommentBody(
    Guid Id,
    Guid RevisionId,
    Guid AuthorId,
    string AuthorDisplayName,
    string Path,
    string Side,
    int Line,
    string Body,
    Guid? ParentId,
    string Status,
    Guid? ResolvedBy,
    DateTime? ResolvedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 评论列表响应 DTO。
/// </summary>
/// <param name="Items">评论列表，按创建时间升序</param>
public sealed record CommentListBody(IReadOnlyList<CommentBody> Items);
