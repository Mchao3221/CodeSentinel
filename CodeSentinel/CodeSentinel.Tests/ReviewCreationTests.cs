using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 创建评审单、唯一索引冲突与审核人通知测试。
/// </summary>
public class ReviewCreationTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 创建评审单应生成 OPEN 状态、版本 1 与待审核的审核人，并写入审计与通知。
    /// </summary>
    [Fact]
    public async Task Creates_review_with_revision_one_and_open_status()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "我的评审",
            "评审说明",
            [context.Reviewer.Id]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        Assert.Equal("OPEN", body.Status);
        Assert.Equal("main", body.BaseBranch);
        Assert.Equal("feature", body.HeadBranch);
        Assert.Equal(context.Source.MainSha, body.BaseSha);
        Assert.Equal(context.HeadSha, body.HeadSha);
        Assert.Equal(1, body.CurrentRevision);
        Assert.Equal(context.Submitter.Id, body.Submitter.Id);

        var revision = Assert.Single(body.Revisions);
        Assert.Equal(1, revision.Seq);
        Assert.Equal(context.HeadSha, revision.HeadSha);

        var reviewer = Assert.Single(body.Reviewers);
        Assert.Equal(context.Reviewer.Id, reviewer.UserId);
        Assert.Equal("PENDING", reviewer.State);

        // 提交人不是审核人：能管理审核人、能关闭，但不能给结论
        Assert.False(body.Permissions.CanDecide);
        Assert.True(body.Permissions.CanManageReviewers);
        Assert.True(body.Permissions.CanClose);
        Assert.False(body.Permissions.CanMarkMerged);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == body.Id);
        Assert.Equal(ReviewStatus.Open, stored.Status);
        Assert.Equal(1, body.CurrentRevision);
        Assert.Equal(
            "REVIEW_CREATED",
            (await db.AuditLogs.AsNoTracking().SingleAsync(m => m.TargetId == body.Id)).Action);
    }

    /// <summary>
    /// 审核人必须是仓库成员，非成员应返回 400 INVALID_REVIEWER。
    /// </summary>
    [Fact]
    public async Task Rejects_reviewer_who_is_not_repo_member()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "包含外部审核人",
            null,
            [context.Reviewer.Id, outsider.Id]);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_REVIEWER", body.Code);
    }

    /// <summary>
    /// 提交人不能把自己选为审核人。
    /// </summary>
    [Fact]
    public async Task Rejects_submitter_as_reviewer()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "提交人自审",
            null,
            [context.Submitter.Id]);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_REVIEWER", body.Code);
    }

    /// <summary>
    /// 审核人列表为空时返回 400 INVALID_REVIEWER。
    /// </summary>
    [Fact]
    public async Task Rejects_empty_reviewer_list()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "没有审核人",
            null,
            []);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("INVALID_REVIEWER", body.Code);
    }

    /// <summary>
    /// 提交分支与目标分支相同（没有领先提交）时返回 400 NO_CHANGES。
    /// </summary>
    [Fact]
    public async Task Rejects_head_not_ahead_of_base()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "没有变更",
            null,
            [context.Reviewer.Id],
            headBranch: BaseBranch);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("NO_CHANGES", body.Code);
    }

    /// <summary>
    /// 同一分支已有活跃评审单时返回 409，并带上已有评审单的主键。
    /// </summary>
    [Fact]
    public async Task Duplicate_active_review_returns_409_with_existing_id()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var first = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "第一次",
            null,
            [context.Reviewer.Id]);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(created);

        var duplicate = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "第二次",
            null,
            [context.Reviewer.Id]);

        var body = await duplicate.Content.ReadFromJsonAsync<ActiveReviewExistsBody>();
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("ACTIVE_REVIEW_EXISTS", body.Code);
        Assert.Equal(created.Id, body.ReviewId);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
    }

    /// <summary>
    /// 每个审核人只收到一条 REVIEW_SUBMITTED 通知，提交人自己不收通知。
    /// </summary>
    [Fact]
    public async Task Notifies_each_reviewer_once()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var secondReviewer = factory.SeedUser(UniqueName("reviewer-b"), TestPassword);
        SeedMember(factory, context.Repo.Id, secondReviewer.Id, isMerger: false);
        var client = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var response = await CreateReviewAsync(
            client,
            context.Repo.Id,
            "通知测试",
            null,
            [context.Reviewer.Id, secondReviewer.Id]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(created);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == created.Id)
            .ToListAsync();

        Assert.Equal(2, notifications.Count);
        Assert.Equal(
            new[] { context.Reviewer.Id, secondReviewer.Id }.OrderBy(m => m).ToList(),
            notifications.Select(m => m.UserId).OrderBy(m => m).ToList());
        Assert.All(notifications, m => Assert.Equal(NotificationType.ReviewSubmitted, m.Type));
        Assert.DoesNotContain(notifications, m => m.UserId == context.Submitter.Id);

        foreach (var notification in notifications)
        {
            using var payload = JsonDocument.Parse(notification.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(created.Id, root.GetProperty("reviewId").GetGuid());
            Assert.Equal("通知测试", root.GetProperty("title").GetString());
            Assert.Equal(context.Repo.Name, root.GetProperty("repoName").GetString());
            Assert.Equal(context.Submitter.DisplayName, root.GetProperty("submitterName").GetString());
            Assert.Equal("feature", root.GetProperty("headBranch").GetString());
            Assert.Equal("main", root.GetProperty("baseBranch").GetString());
        }
    }

    /// <summary>
    /// 列表 scope 按提交人、审核人与仓库访问权限过滤，且支持状态过滤。
    /// </summary>
    [Fact]
    public async Task List_scopes_respect_membership_and_assignment()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var created = await CreateReviewAsync(
            submitter,
            context.Repo.Id,
            "列表测试",
            null,
            [context.Reviewer.Id]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(detail);

        var mine = await submitter.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=mine");
        Assert.NotNull(mine);
        Assert.Contains(mine.Items, m => m.Id == detail.Id);

        var submitterToReview = await submitter.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=to_review");
        Assert.NotNull(submitterToReview);
        Assert.DoesNotContain(submitterToReview.Items, m => m.Id == detail.Id);

        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var toReview = await reviewer.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=to_review");
        Assert.NotNull(toReview);
        var item = Assert.Single(toReview.Items);
        Assert.Equal(1, item.ReviewersTotal);
        Assert.Equal(0, item.ReviewersApproved);
        Assert.Equal(1, item.CurrentRevision);
        Assert.Equal(context.Repo.Name, item.Repo.Name);

        var reviewerMine = await reviewer.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=mine");
        Assert.NotNull(reviewerMine);
        Assert.Empty(reviewerMine.Items);

        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var outsiderList = await outsiderClient.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=repo");
        Assert.NotNull(outsiderList);
        Assert.Empty(outsiderList.Items);

        var openOnly = await reviewer.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=repo&status=open");
        Assert.NotNull(openOnly);
        Assert.Single(openOnly.Items);

        var closedOnly = await reviewer.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=repo&status=CLOSED");
        Assert.NotNull(closedOnly);
        Assert.Empty(closedOnly.Items);

        var admin = await factory.LoginAsAdminAsync();
        var adminList = await admin.GetFromJsonAsync<ReviewListBody>("/api/reviews?scope=repo");
        Assert.NotNull(adminList);
        Assert.Contains(adminList.Items, m => m.Id == detail.Id);
    }

    /// <summary>
    /// 详情接口要求仓库访问权限，并按当前用户计算操作权限。
    /// </summary>
    [Fact]
    public async Task Detail_requires_repo_access_and_reports_permissions()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedReviewContextAsync(factory, source);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var created = await CreateReviewAsync(
            submitter,
            context.Repo.Id,
            "详情测试",
            null,
            [context.Reviewer.Id]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(detail);

        var reviewer = await factory.LoginAsync(context.Reviewer.Username, TestPassword);
        var reviewerDetail = await reviewer.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{detail.Id}");
        Assert.NotNull(reviewerDetail);
        Assert.True(reviewerDetail.Permissions.CanDecide);
        Assert.False(reviewerDetail.Permissions.CanManageReviewers);
        Assert.False(reviewerDetail.Permissions.CanClose);
        Assert.False(reviewerDetail.Permissions.CanMarkMerged);

        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var denied = await outsiderClient.GetAsync($"/api/reviews/{detail.Id}");
        var error = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
    }

    /// <summary>
    /// 准备仓库、提交人、审核人、成员关系与镜像中的 feature 分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>评审上下文</returns>
    private static async Task<ReviewContext> SeedReviewContextAsync(
        TestAppFactory factory,
        TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewer = factory.SeedUser(UniqueName("reviewer"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("review"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewer.Id, isMerger: false);
        var headSha = await source.CreateBranchAsync(HeadBranch, 2);
        await FetchMirrorAsync(factory, repo);
        return new ReviewContext(source, repo, submitter, reviewer, headSha);
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
    /// 调用创建评审单接口。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="title">标题</param>
    /// <param name="description">说明</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <param name="headBranch">提交分支</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        string title,
        string? description,
        IReadOnlyList<Guid> reviewerIds,
        string headBranch = HeadBranch)
    {
        return client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch,
                baseBranch = BaseBranch,
                title,
                description,
                reviewerIds
            });
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
    /// 测试上下文：真实 git 仓库、数据库仓库与相关用户。
    /// </summary>
    /// <param name="Source">真实 git 测试仓库</param>
    /// <param name="Repo">数据库仓库</param>
    /// <param name="Submitter">提交人</param>
    /// <param name="Reviewer">审核人</param>
    /// <param name="HeadSha">feature 分支提交</param>
    private sealed record ReviewContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User Reviewer,
        string HeadSha);
}

/// <summary>
/// 评审详情响应 DTO。
/// </summary>
public sealed record ReviewDetailBody(
    Guid Id,
    ReviewRepoBody Repo,
    string BaseBranch,
    string HeadBranch,
    ReviewUserBody Submitter,
    string Title,
    string? Description,
    string Status,
    string BaseSha,
    string HeadSha,
    int CurrentRevision,
    IReadOnlyList<ReviewRevisionBody> Revisions,
    IReadOnlyList<ReviewReviewerBody> Reviewers,
    ReviewPermissionsBody Permissions,
    bool MergeHint,
    DateTime? MergedAt,
    string? MergeCommitSha,
    string? CloseReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 评审单关联仓库 DTO。
/// </summary>
/// <param name="Id">仓库主键</param>
/// <param name="Name">仓库名称</param>
public sealed record ReviewRepoBody(Guid Id, string Name);

/// <summary>
/// 评审单关联用户 DTO。
/// </summary>
/// <param name="Id">用户主键</param>
/// <param name="DisplayName">显示名</param>
public sealed record ReviewUserBody(Guid Id, string DisplayName);

/// <summary>
/// 评审版本 DTO。
/// </summary>
/// <param name="Id">版本主键</param>
/// <param name="Seq">版本号</param>
/// <param name="HeadSha">版本终点提交</param>
/// <param name="CreatedAt">创建时间</param>
public sealed record ReviewRevisionBody(Guid Id, int Seq, string HeadSha, DateTime CreatedAt);

/// <summary>
/// 评审审核人 DTO。
/// </summary>
/// <param name="UserId">审核人主键</param>
/// <param name="DisplayName">显示名</param>
/// <param name="State">结论状态</param>
/// <param name="Note">结论附言</param>
public sealed record ReviewReviewerBody(Guid UserId, string DisplayName, string State, string? Note);

/// <summary>
/// 评审单操作权限 DTO。
/// </summary>
/// <param name="CanDecide">能否给结论</param>
/// <param name="CanManageReviewers">能否管理审核人</param>
/// <param name="CanClose">能否关闭</param>
/// <param name="CanMarkMerged">能否标记已合并</param>
public sealed record ReviewPermissionsBody(
    bool CanDecide,
    bool CanManageReviewers,
    bool CanClose,
    bool CanMarkMerged);

/// <summary>
/// 评审列表项 DTO。
/// </summary>
/// <param name="Id">评审单主键</param>
/// <param name="Repo">所属仓库</param>
/// <param name="Title">标题</param>
/// <param name="Status">状态</param>
/// <param name="BaseBranch">目标分支</param>
/// <param name="HeadBranch">提交分支</param>
/// <param name="Submitter">提交人</param>
/// <param name="ReviewersTotal">在册审核人数</param>
/// <param name="ReviewersApproved">已通过人数</param>
/// <param name="CurrentRevision">当前版本号</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ReviewListItemBody(
    Guid Id,
    ReviewRepoBody Repo,
    string Title,
    string Status,
    string BaseBranch,
    string HeadBranch,
    ReviewUserBody Submitter,
    int ReviewersTotal,
    int ReviewersApproved,
    int CurrentRevision,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 评审列表响应 DTO。
/// </summary>
/// <param name="Items">当前页数据</param>
/// <param name="Total">总条数</param>
/// <param name="Page">当前页码</param>
/// <param name="PageSize">每页条数</param>
public sealed record ReviewListBody(
    IReadOnlyList<ReviewListItemBody> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// 活跃评审单冲突响应 DTO。
/// </summary>
/// <param name="Code">错误码</param>
/// <param name="Message">错误信息</param>
/// <param name="TraceId">请求追踪标识</param>
/// <param name="ReviewId">已存在的活跃评审单主键</param>
public sealed record ActiveReviewExistsBody(
    string Code,
    string Message,
    string TraceId,
    Guid ReviewId);
