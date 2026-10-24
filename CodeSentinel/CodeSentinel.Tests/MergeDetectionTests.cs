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
/// 合并检测、分支删除关闭与手工标记合并测试。
/// </summary>
public class MergeDetectionTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 评审分支的 head 已成为目标分支祖先时，评审单应置为 MERGED，记录合并提交并通知提交人与审核人。
    /// </summary>
    [Fact]
    public async Task Ancestor_head_marks_merged_and_notifies()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "自动检测合并", [context.ReviewerA.Id, context.ReviewerB.Id]);
        await ApproveAllAsync(reviewerA, reviewerB, review.Id);

        var mergeCommit = await source.MergeBranchIntoMainAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);

        var result = await CheckMergeAsync(factory, review.Id);

        Assert.True(result.Merged);
        Assert.Equal(mergeCommit, result.MergeCommitSha);
        Assert.False(result.MergeHint);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Merged, stored.Status);
        Assert.Equal(mergeCommit, stored.MergeCommitSha);
        Assert.NotNull(stored.MergedAt);
        Assert.False(stored.MergeHint);
        Assert.Equal(0, stored.MergeHintCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.Merged)
            .ToListAsync();
        Assert.Equal(3, notifications.Count);
        Assert.Equal(
            new[] { context.Submitter.Id, context.ReviewerA.Id, context.ReviewerB.Id }
                .OrderBy(m => m)
                .ToList(),
            notifications.Select(m => m.UserId).OrderBy(m => m).ToList());

        var audit = Assert.Single(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == review.Id && m.Action == "REVIEW_MERGED")
                .ToListAsync());
        Assert.Null(audit.ActorId);
    }

    /// <summary>
    /// 未检测到合并时保持 APPROVED，连续三次同步后置合并提示且计数为 3。
    /// </summary>
    [Fact]
    public async Task Non_ancestor_keeps_approved_and_sets_hint_after_three_syncs()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "三次未合并", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        for (var sync = 1; sync <= 3; sync++)
        {
            await SyncMergeStatesAsync(factory, context.Repo.Id);

            var stored = await ReadReviewAsync(factory, review.Id);
            Assert.Equal(ReviewStatus.Approved, stored.Status);
            Assert.Equal(sync, stored.MergeHintCount);
            Assert.Equal(sync >= 3, stored.MergeHint);
        }

        Assert.Null((await ReadReviewAsync(factory, review.Id)).MergedAt);
    }

    /// <summary>
    /// 普通仓库成员手工标记合并返回 403，非成员返回 404，管理员可以完成标记。
    /// </summary>
    [Fact]
    public async Task Manual_mark_requires_merger_or_admin()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "手工标记权限", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        var denied = await reviewer.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { });
        var deniedBody = await denied.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.NotNull(deniedBody);
        Assert.Equal("FORBIDDEN", deniedBody.Code);
        Assert.Equal(ReviewStatus.Approved, (await ReadReviewAsync(factory, review.Id)).Status);

        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);
        var hidden = await outsiderClient.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { });
        var hiddenBody = await hidden.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.NotNull(hiddenBody);
        Assert.Equal("NOT_FOUND", hiddenBody.Code);

        var admin = await factory.LoginAsAdminAsync();
        var allowed = await admin.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var detail = await allowed.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(detail);
        Assert.Equal("MERGED", detail.Status);
        Assert.Equal(context.Source.MainSha, detail.MergeCommitSha);
    }

    /// <summary>
    /// 合并人可以携带自定义合并提交 SHA 手工标记，审计记录操作人。
    /// </summary>
    [Fact]
    public async Task Manual_mark_accepts_optional_sha()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var merger = await factory.LoginAsync(context.Merger.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "手工标记 SHA", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        const string mergeCommit = "0123456789abcdef0123456789abcdef01234567";
        var response = await merger.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { mergeCommitSha = mergeCommit });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(detail);
        Assert.Equal("MERGED", detail.Status);
        Assert.Equal(mergeCommit, detail.MergeCommitSha);
        Assert.NotNull(detail.MergedAt);
        Assert.False(detail.MergeHint);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = Assert.Single(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == review.Id && m.Action == "REVIEW_MERGED")
                .ToListAsync());
        Assert.Equal(context.Merger.Id, audit.ActorId);
    }

    /// <summary>
    /// 已关闭的评审单不允许再手工标记为已合并，返回 409 REVIEW_NOT_ACTIVE。
    /// </summary>
    [Fact]
    public async Task Cannot_mark_closed_review_as_merged()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var merger = await factory.LoginAsync(context.Merger.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "关闭后标记", [context.ReviewerA.Id]);

        var closed = await submitter.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/close", new { reason = "不再需要" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(ReviewStatus.Closed, (await ReadReviewAsync(factory, review.Id)).Status);

        var response = await merger.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/mark-merged", new { });
        var error = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("REVIEW_NOT_ACTIVE", error.Code);
        Assert.Equal(ReviewStatus.Closed, (await ReadReviewAsync(factory, review.Id)).Status);
    }

    /// <summary>
    /// 分支合并后删除，同步时应识别为已合并并保持 MERGED，而不是按分支删除关闭。
    /// </summary>
    [Fact]
    public async Task Deleted_branch_after_merge_keeps_merged_status()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "合并后删分支", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        var mergeCommit = await source.MergeBranchIntoMainAsync(HeadBranch);
        await source.DeleteRemoteBranchAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);

        await SyncActiveReviewsAsync(factory, context.Repo.Id);
        await SyncMergeStatesAsync(factory, context.Repo.Id);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Merged, stored.Status);
        Assert.Equal(mergeCommit, stored.MergeCommitSha);
        Assert.Null(stored.CloseReason);
    }

    /// <summary>
    /// 活跃评审单的分支被删除后应关闭为 CLOSED，原因 BRANCH_DELETED，并通知提交人与审核人。
    /// </summary>
    [Fact]
    public async Task Deleted_branch_closes_active_review()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "分支被删除", [context.ReviewerA.Id, context.ReviewerB.Id]);

        await source.DeleteRemoteBranchAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);
        await SyncMergeStatesAsync(factory, context.Repo.Id);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Closed, stored.Status);
        Assert.Equal("BRANCH_DELETED", stored.CloseReason);
        Assert.NotNull(stored.ClosedAt);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.Closed)
            .ToListAsync();
        Assert.Equal(3, notifications.Count);
        Assert.Equal(
            new[] { context.Submitter.Id, context.ReviewerA.Id, context.ReviewerB.Id }
                .OrderBy(m => m)
                .ToList(),
            notifications.Select(m => m.UserId).OrderBy(m => m).ToList());

        var audit = Assert.Single(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == review.Id && m.Action == "REVIEW_CLOSED")
                .ToListAsync());
        Assert.Null(audit.ActorId);
        Assert.Contains("BRANCH_DELETED", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 新版本生成后应清空合并提示与计数（P34）。
    /// </summary>
    [Fact]
    public async Task New_revision_resets_merge_hint()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "新版本清空提示", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        for (var sync = 0; sync < 3; sync++)
        {
            await SyncMergeStatesAsync(factory, context.Repo.Id);
        }

        var hinted = await ReadReviewAsync(factory, review.Id);
        Assert.True(hinted.MergeHint);
        Assert.Equal(3, hinted.MergeHintCount);

        var newHead = await source.CommitOnBranchAsync(HeadBranch);
        await FetchMirrorAsync(factory, context.Repo);
        Assert.True(await AddRevisionAsync(factory, review.Id, newHead));

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Open, stored.Status);
        Assert.False(stored.MergeHint);
        Assert.Equal(0, stored.MergeHintCount);
    }

    /// <summary>
    /// 审核人撤回结论使评审单从通过回退时，应清空旧的合并提示，避免过期 squash 提示再次出现。
    /// </summary>
    [Fact]
    public async Task Withdrawal_resets_merge_hint()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "撤回清空提示", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        for (var sync = 0; sync < 3; sync++)
        {
            await SyncMergeStatesAsync(factory, context.Repo.Id);
        }

        var hinted = await ReadReviewAsync(factory, review.Id);
        Assert.True(hinted.MergeHint);
        Assert.Equal(3, hinted.MergeHintCount);

        var withdraw = await reviewer.PostJsonWithCsrfAsync(
            $"/api/reviews/{review.Id}/decisions", new { state = "PENDING", note = (string?)null });
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Open, stored.Status);
        Assert.False(stored.MergeHint);
        Assert.Equal(0, stored.MergeHintCount);
    }

    /// <summary>
    /// 后台同步 fetch 成功后应自动检测已合并的评审单。
    /// </summary>
    [Fact]
    public async Task Worker_detects_merge_after_fetch()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, "后台检测合并", [context.ReviewerA.Id]);
        await ApproveAllAsync(reviewer, null, review.Id);

        var mergeCommit = await source.MergeBranchIntoMainAsync(HeadBranch);
        var worker = factory.Services.GetRequiredService<MirrorSyncWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        var stored = await ReadReviewAsync(factory, review.Id);
        Assert.Equal(ReviewStatus.Merged, stored.Status);
        Assert.Equal(mergeCommit, stored.MergeCommitSha);
        Assert.NotNull((await ReadRepoAsync(factory, context.Repo.Id)).LastFetchedAt);
    }

    /// <summary>
    /// 准备仓库、提交人、两名审核人与合并人，并把 feature 分支拉进镜像。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="source">真实 git 测试仓库</param>
    /// <returns>合并检测上下文</returns>
    private static async Task<MergeContext> SeedContextAsync(
        TestAppFactory factory,
        TestGitRepo source)
    {
        var submitter = factory.SeedUser(UniqueName("submitter"), TestPassword);
        var reviewerA = factory.SeedUser(UniqueName("reviewer-a"), TestPassword);
        var reviewerB = factory.SeedUser(UniqueName("reviewer-b"), TestPassword);
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("merge"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerA.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerB.Id, isMerger: false);
        SeedMember(factory, repo.Id, merger.Id, isMerger: true);
        var headSha = await source.CreateBranchAsync(HeadBranch, 2);
        await FetchMirrorAsync(factory, repo);
        return new MergeContext(
            source, repo, submitter, reviewerA, reviewerB, merger, headSha);
    }

    /// <summary>
    /// 直接写入仓库成员关系。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="isMerger">是否合并人</param>
    private static void SeedMember(
        TestAppFactory factory,
        Guid repoId,
        Guid userId,
        bool isMerger)
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
    /// 两名审核人依次通过评审单，第二名可为空。
    /// </summary>
    /// <param name="reviewerA">第一审核人客户端</param>
    /// <param name="reviewerB">第二审核人客户端，可为空</param>
    /// <param name="reviewId">评审单主键</param>
    /// <returns></returns>
    private static async Task ApproveAllAsync(
        HttpClient reviewerA,
        HttpClient? reviewerB,
        Guid reviewId)
    {
        var first = await reviewerA.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/decisions",
            new { state = "APPROVED", note = (string?)null });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        if (reviewerB is null)
        {
            return;
        }

        var second = await reviewerB.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/decisions",
            new { state = "APPROVED", note = (string?)null });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    /// <summary>
    /// 直接调用合并检测服务检查单个评审单。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <returns>检测结果</returns>
    private static async Task<MergeCheckResult> CheckMergeAsync(
        TestAppFactory factory,
        Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MergeDetectionService>();
        return await service.CheckAsync(reviewId, CancellationToken.None);
    }

    /// <summary>
    /// 直接调用合并状态同步服务。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <returns></returns>
    private static async Task SyncMergeStatesAsync(TestAppFactory factory, Guid repoId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MergeDetectionService>();
        await service.SyncReviewMergeStatesAsync(repoId, CancellationToken.None);
    }

    /// <summary>
    /// 直接调用活跃评审单同步服务。
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
    /// <param name="Merger">仓库合并人</param>
    /// <param name="HeadSha">评审创建时的 feature 分支提交</param>
    private sealed record MergeContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User ReviewerA,
        User ReviewerB,
        User Merger,
        string HeadSha);
}
