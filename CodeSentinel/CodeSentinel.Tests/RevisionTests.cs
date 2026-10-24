using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Reviews;
using CodeSentinel.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 新提交生成新版本、结论作废、新提交通知、刷新与版本列表端点测试。
/// </summary>
public class RevisionTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 新提交应生成版本 2，把评审单拉回 OPEN，并清空全部在册审核人的结论。
    /// </summary>
    [Fact]
    public async Task New_head_creates_revision_two_and_resets_all_verdicts()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "新提交测试", [context.ReviewerA.Id, context.ReviewerB.Id]);

        await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var changes = await DecideAsync(reviewerB, review.Id, "CHANGES_REQUESTED", "请补充测试");
        var before = await changes.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(before);
        Assert.Equal("CHANGES_REQUESTED", before.Status);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var detail = await reviewerB.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{review.Id}");
        Assert.NotNull(detail);
        Assert.Equal("OPEN", detail.Status);
        Assert.Equal(2, detail.CurrentRevision);
        Assert.Equal(newHead, detail.HeadSha);
        Assert.Equal(new[] { 1, 2 }, detail.Revisions.Select(m => m.Seq).ToArray());
        Assert.Equal(newHead, detail.Revisions[1].HeadSha);
        Assert.All(detail.Reviewers, m => Assert.Equal("PENDING", m.State));
        Assert.All(detail.Reviewers, m => Assert.Null(m.Note));
        Assert.True(detail.Permissions.CanDecide);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionTwo = await db.ReviewRevisions.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.Seq == 2);
        var revisionOne = await db.ReviewRevisions.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.Seq == 1);
        Assert.Equal(revisionOne.BaseSha, revisionTwo.BaseSha);
        Assert.All(
            await db.ReviewReviewers.AsNoTracking()
                .Where(m => m.ReviewId == review.Id && m.State != ReviewerState.Removed)
                .ToListAsync(),
            m =>
            {
                Assert.Equal(ReviewerState.Pending, m.State);
                Assert.Equal(revisionTwo.Id, m.StateRevisionId);
                Assert.Null(m.Note);
            });
        Assert.Contains(
            await db.AuditLogs.AsNoTracking().Where(m => m.TargetId == review.Id).ToListAsync(),
            m => m.Action == "REVISION_CREATED");
    }

    /// <summary>
    /// 已通过的评审单在新提交后回到 OPEN，通过时间被清空。
    /// </summary>
    [Fact]
    public async Task Approved_review_returns_to_open_after_new_commit()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "通过后新提交", [context.ReviewerA.Id]);

        var approved = await DecideAsync(reviewer, review.Id, "APPROVED", null);
        var approvedBody = await approved.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(approvedBody);
        Assert.Equal("APPROVED", approvedBody.Status);
        Assert.NotNull((await ReadReviewAsync(factory, review.Id)).ApprovedAt);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var detail = await reviewer.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{review.Id}");
        Assert.NotNull(detail);
        Assert.Equal("OPEN", detail.Status);
        Assert.Equal(2, detail.CurrentRevision);
        Assert.Equal("PENDING", Assert.Single(detail.Reviewers).State);
        Assert.True(detail.Permissions.CanDecide);
        Assert.Null((await ReadReviewAsync(factory, review.Id)).ApprovedAt);
    }

    /// <summary>
    /// 新版本沿用当前版本的基线提交，目标分支前进也不改变基线。
    /// </summary>
    [Fact]
    public async Task Base_sha_is_not_changed_by_new_head()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "基线不变", [context.ReviewerA.Id]);
        var originalBase = review.BaseSha;
        Assert.Equal(context.Source.MainSha, originalBase);

        var newMain = await source.CommitOnMainAsync();
        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        Assert.Equal(originalBase, (await ReadReviewAsync(factory, review.Id)).BaseSha);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionTwo = await db.ReviewRevisions.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.Seq == 2);
        Assert.Equal(originalBase, revisionTwo.BaseSha);
        Assert.NotEqual(newMain, revisionTwo.BaseSha);
        Assert.Equal(newHead, revisionTwo.HeadSha);
    }

    /// <summary>
    /// 同一 head 重复同步不产生第二个版本，也不重复通知与审计。
    /// </summary>
    [Fact]
    public async Task Same_head_is_idempotent()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "重复同步", [context.ReviewerA.Id, context.ReviewerB.Id]);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));
        Assert.False(await AddRevisionAsync(factory, review.Id, newHead));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.ReviewRevisions.AsNoTracking()
            .CountAsync(m => m.ReviewId == review.Id));
        Assert.Equal(1, await db.AuditLogs.AsNoTracking()
            .CountAsync(m => m.TargetId == review.Id && m.Action == "REVISION_CREATED"));

        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.NewCommits)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.Equal(
            new[] { context.ReviewerA.Id, context.ReviewerB.Id }.OrderBy(m => m).ToList(),
            notifications.Select(m => m.UserId).OrderBy(m => m).ToList());
    }

    /// <summary>
    /// 新提交只通知在册审核人，每人一条，payload 带新 head SHA。
    /// </summary>
    [Fact]
    public async Task Notifies_reviewers_with_new_commits()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var reviewerC = factory.SeedUser(UniqueName("reviewer-c"), TestPassword);
        SeedMember(factory, context.Repo.Id, reviewerC.Id, isMerger: false);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter,
            context.Repo.Id,
            "通知新提交",
            [context.ReviewerA.Id, context.ReviewerB.Id, reviewerC.Id]);

        var removed = await submitter.DeleteWithCsrfAsync(
            $"/api/reviews/{review.Id}/reviewers/{reviewerC.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.NewCommits)
            .ToListAsync();

        Assert.Equal(2, notifications.Count);
        Assert.Equal(
            new[] { context.ReviewerA.Id, context.ReviewerB.Id }.OrderBy(m => m).ToList(),
            notifications.Select(m => m.UserId).OrderBy(m => m).ToList());
        Assert.DoesNotContain(notifications, m => m.UserId == reviewerC.Id);
        Assert.DoesNotContain(notifications, m => m.UserId == context.Submitter.Id);

        foreach (var notification in notifications)
        {
            using var payload = JsonDocument.Parse(notification.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(review.Id, root.GetProperty("reviewId").GetGuid());
            Assert.Equal("通知新提交", root.GetProperty("title").GetString());
            Assert.Equal(context.Repo.Name, root.GetProperty("repoName").GetString());
            Assert.Equal(HeadBranch, root.GetProperty("headBranch").GetString());
            Assert.Equal(BaseBranch, root.GetProperty("baseBranch").GetString());
            Assert.Equal(newHead, root.GetProperty("headSha").GetString());
        }
    }

    /// <summary>
    /// 刷新端点先 fetch 远端，再为新 head 生成版本 2。
    /// </summary>
    [Fact]
    public async Task Refresh_endpoint_fetches_then_updates_revision()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "刷新评审单", [context.ReviewerA.Id]);

        // 只推远端，不主动 fetch，刷新端点必须自己完成同步
        var newHead = await source.CommitOnBranchAsync(HeadBranch);

        var response = await submitter.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(detail);
        Assert.Equal("OPEN", detail.Status);
        Assert.Equal(2, detail.CurrentRevision);
        Assert.Equal(newHead, detail.HeadSha);
        Assert.Equal(2, detail.Revisions.Count);
        Assert.Equal(newHead, detail.Revisions[1].HeadSha);
        Assert.Equal("PENDING", Assert.Single(detail.Reviewers).State);

        Assert.NotNull((await ReadRepoAsync(factory, context.Repo.Id)).LastFetchedAt);
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        Assert.Equal(
            newHead,
            await mirror.GetHeadAsync(context.Repo.Id, HeadBranch, CancellationToken.None));

        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var denied = await outsiderClient.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/refresh", new { });
        var error = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
    }

    /// <summary>
    /// 版本列表端点按版本号升序返回全部版本，非仓库成员按不存在处理。
    /// </summary>
    [Fact]
    public async Task Revisions_endpoint_lists_versions_in_order_and_requires_access()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "版本列表", [context.ReviewerA.Id]);

        var initial = await reviewer.GetFromJsonAsync<RevisionListBody>(
            $"/api/reviews/{review.Id}/revisions");
        Assert.NotNull(initial);
        var only = Assert.Single(initial.Items);
        Assert.Equal(1, only.Seq);
        Assert.Equal(context.HeadSha, only.HeadSha);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var updated = await reviewer.GetFromJsonAsync<RevisionListBody>(
            $"/api/reviews/{review.Id}/revisions");
        Assert.NotNull(updated);
        Assert.Equal(new[] { 1, 2 }, updated.Items.Select(m => m.Seq).ToArray());
        Assert.Equal(newHead, updated.Items[1].HeadSha);

        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var denied = await outsiderClient.GetAsync($"/api/reviews/{review.Id}/revisions");
        var error = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
    }

    /// <summary>
    /// 后台同步在 fetch 成功后应跟进活跃评审单的新提交，且不记为同步失败。
    /// </summary>
    [Fact]
    public async Task Worker_syncs_active_reviews_after_fetch()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "后台同步", [context.ReviewerA.Id]);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        var worker = factory.Services.GetRequiredService<MirrorSyncWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        var detail = await reviewer.GetFromJsonAsync<ReviewDetailBody>($"/api/reviews/{review.Id}");
        Assert.NotNull(detail);
        Assert.Equal(2, detail.CurrentRevision);
        Assert.Equal(newHead, detail.HeadSha);
        Assert.Equal("PENDING", Assert.Single(detail.Reviewers).State);

        var repo = await ReadRepoAsync(factory, context.Repo.Id);
        Assert.NotNull(repo.LastFetchedAt);
        Assert.Null(repo.LastError);
        Assert.Equal(0, repo.FailureCount);
    }

    /// <summary>
    /// 评审单存储的分支名与镜像规范名大小写不同时，仍应识别同一分支的新提交。
    /// </summary>
    [Fact]
    public async Task Sync_matches_head_branch_case_insensitively()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "大小写分支", [context.ReviewerA.Id]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Reviews
                .Where(m => m.Id == review.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(r => r.HeadBranch, "FEATURE"));
        }

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);
        await SyncActiveReviewsAsync(factory, context.Repo.Id);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(newHead, stored.HeadSha);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await verifyDb.ReviewRevisions.AsNoTracking()
            .CountAsync(m => m.ReviewId == review.Id));
    }

    /// <summary>
    /// 准备仓库、提交人、两名审核人以及镜像中的 feature 分支。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>评审上下文</returns>
    private static async Task<ReviewContext> SeedContextAsync(
        TestAppFactory factory,
        TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewerA = factory.SeedUser(UniqueName("reviewer-a"), TestPassword);
        var reviewerB = factory.SeedUser(UniqueName("reviewer-b"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("revision"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerA.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerB.Id, isMerger: false);
        var headSha = await source.CreateBranchAsync(HeadBranch, 2);
        await FetchMirrorAsync(factory, repo);
        return new ReviewContext(source, repo, submitter, reviewerA, reviewerB, headSha);
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
    /// <param name="repoId">仓库主键</param>
    /// <param name="title">标题</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        string title,
        IReadOnlyList<Guid> reviewerIds)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch = HeadBranch,
                baseBranch = BaseBranch,
                title,
                description = (string?)null,
                reviewerIds
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// 提交审核结论。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="state">结论状态</param>
    /// <param name="note">结论附言</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> DecideAsync(
        HttpClient client,
        Guid reviewId,
        string state,
        string? note)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/decisions",
            new { state, note });
    }

    /// <summary>
    /// 直接调用版本服务生成新版本。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="newHeadSha">新 head 提交</param>
    /// <returns>是否生成了新版本</returns>
    private static async Task<bool> AddRevisionAsync(
        TestAppFactory factory,
        Guid reviewId,
        string newHeadSha)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RevisionService>();
        return await service.AddRevisionAsync(reviewId, newHeadSha, CancellationToken.None);
    }

    /// <summary>
    /// 直接调用活跃评审单同步。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <returns></returns>
    private static async Task SyncActiveReviewsAsync(TestAppFactory factory, Guid repoId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RevisionService>();
        await service.SyncActiveReviewsAsync(repoId, CancellationToken.None);
    }

    /// <summary>
    /// 读取评审单最新状态。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <returns>只读评审单</returns>
    private static async Task<Review> ReadReviewAsync(TestAppFactory factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == reviewId);
    }

    /// <summary>
    /// 读取仓库最新状态。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <returns>只读仓库</returns>
    private static async Task<Repo> ReadRepoAsync(TestAppFactory factory, Guid repoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Repos.AsNoTracking().SingleAsync(m => m.Id == repoId);
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
    /// <param name="ReviewerA">第一审核人</param>
    /// <param name="ReviewerB">第二审核人</param>
    /// <param name="HeadSha">评审创建时的 feature 分支提交</param>
    private sealed record ReviewContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User ReviewerA,
        User ReviewerB,
        string HeadSha);
}

/// <summary>
/// 评审版本列表响应 DTO。
/// </summary>
/// <param name="Items">版本列表，按版本号升序</param>
public sealed record RevisionListBody(IReadOnlyList<ReviewRevisionBody> Items);
