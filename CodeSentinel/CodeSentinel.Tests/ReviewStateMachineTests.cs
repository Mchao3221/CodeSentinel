using System.Net;
using System.Net.Http.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 审核结论、状态机与审核人管理测试。
/// </summary>
public class ReviewStateMachineTests
{
    private const string TestPassword = "Passw0rd!";
    private const string BaseBranch = "main";
    private const string HeadBranch = "feature";

    /// <summary>
    /// 状态机重算的三组典型输入。
    /// </summary>
    public static TheoryData<ReviewerState[], ReviewStatus> Cases => new()
    {
        { new[] { ReviewerState.Approved, ReviewerState.Pending }, ReviewStatus.Open },
        { new[] { ReviewerState.Approved, ReviewerState.Approved }, ReviewStatus.Approved },
        { new[] { ReviewerState.Approved, ReviewerState.ChangesRequested }, ReviewStatus.ChangesRequested },
    };

    /// <summary>
    /// 状态机按审核人结论映射评审单状态。
    /// </summary>
    /// <param name="states">审核人结论</param>
    /// <param name="expected">期望状态</param>
    [Theory, MemberData(nameof(Cases))]
    public void Recompute_maps_states(ReviewerState[] states, ReviewStatus expected)
    {
        Assert.Equal(expected, ReviewStateMachine.Recompute(states));
    }

    /// <summary>
    /// 未被分配的仓库成员不能给出结论。
    /// </summary>
    [Fact]
    public async Task Non_assigned_reviewer_cannot_decide()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var outsider = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var response = await DecideAsync(outsider, review.Id, "APPROVED", null);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("NOT_A_REVIEWER", body.Code);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.ReviewDecisions.AsNoTracking()
            .Where(m => m.ReviewId == review.Id)
            .ToListAsync());
    }

    /// <summary>
    /// 提交人即使被手工加入审核人名单也不能给出结论。
    /// </summary>
    [Fact]
    public async Task Submitter_cannot_decide()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);
        SeedSubmitterAsReviewer(factory, review.Id, context.Submitter.Id);

        var response = await DecideAsync(submitter, review.Id, "APPROVED", null);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("SUBMITTER_CANNOT_DECIDE", body.Code);
    }

    /// <summary>
    /// 通过后再次提交 PENDING 表示撤回，评审单回到 OPEN。
    /// </summary>
    [Fact]
    public async Task Withdraw_from_approved_returns_to_open()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var approved = await DecideAsync(reviewer, review.Id, "APPROVED", "没问题");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedBody = await approved.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(approvedBody);
        Assert.Equal("APPROVED", approvedBody.Status);

        var withdrawn = await DecideAsync(reviewer, review.Id, "PENDING", null);
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        var withdrawnBody = await withdrawn.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(withdrawnBody);
        Assert.Equal("OPEN", withdrawnBody.Status);

        var reviewerRow = Assert.Single(withdrawnBody.Reviewers);
        Assert.Equal("PENDING", reviewerRow.State);
        Assert.Null(reviewerRow.Note);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var decisions = await db.ReviewDecisions.AsNoTracking()
            .Where(m => m.ReviewId == review.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, m => m.State == ReviewerState.Approved);
        Assert.Contains(decisions, m => m.State == ReviewerState.Pending);

        var notifications = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.UserId == context.Submitter.Id)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.Contains(notifications, m => m.Type == NotificationType.Approved);
        Assert.Single(notifications, m => m.Type == NotificationType.DecisionWithdrawn);
    }

    /// <summary>
    /// 全部审核人通过时，合并人与提交人各收到一条通过通知，重复操作不重复通知。
    /// </summary>
    [Fact]
    public async Task Approving_all_notifies_mergers_once()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        var first = await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Notifications.AsNoTracking()
                .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.Approved)
                .ToListAsync());
        }

        var second = await DecideAsync(reviewerB, review.Id, "APPROVED", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var approvedBody = await second.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(approvedBody);
        Assert.Equal("APPROVED", approvedBody.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var approved = await db.Notifications.AsNoTracking()
                .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.Approved)
                .ToListAsync();
            Assert.Equal(2, approved.Count);
            Assert.Single(approved, m => m.UserId == context.Merger.Id);
            Assert.Single(approved, m => m.UserId == context.Submitter.Id);
        }

        var repeated = await DecideAsync(reviewerB, review.Id, "APPROVED", null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var approvedCount = await db.Notifications.AsNoTracking()
                .CountAsync(m => m.ReviewId == review.Id && m.Type == NotificationType.Approved);
            Assert.Equal(2, approvedCount);
        }
    }

    /// <summary>
    /// 已关闭或已合并的评审单不能再给出结论。
    /// </summary>
    [Fact]
    public async Task Cannot_decide_on_merged_or_closed_review()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var closed = await CloseAsync(submitter, review.Id, "不再需要");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var onClosed = await DecideAsync(reviewer, review.Id, "APPROVED", null);
        var closedBody = await onClosed.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Conflict, onClosed.StatusCode);
        Assert.NotNull(closedBody);
        Assert.Equal("REVIEW_NOT_ACTIVE", closedBody.Code);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Reviews.SingleAsync(m => m.Id == review.Id);
            stored.Status = ReviewStatus.Merged;
            await db.SaveChangesAsync();
        }

        var onMerged = await DecideAsync(reviewer, review.Id, "APPROVED", null);
        var mergedBody = await onMerged.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.Conflict, onMerged.StatusCode);
        Assert.NotNull(mergedBody);
        Assert.Equal("REVIEW_NOT_ACTIVE", mergedBody.Code);
    }

    /// <summary>
    /// 提交人可以关闭自己的评审单，管理员可以关闭任意评审单。
    /// </summary>
    [Fact]
    public async Task Submitter_can_close_own_review_admin_can_close_any()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);

        var own = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);
        var ownClose = await CloseAsync(submitter, own.Id, "需求变更");
        var ownBody = await ownClose.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.Equal(HttpStatusCode.OK, ownClose.StatusCode);
        Assert.NotNull(ownBody);
        Assert.Equal("CLOSED", ownBody.Status);
        Assert.Equal("需求变更", ownBody.CloseReason);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == own.Id);
            Assert.Equal(ReviewStatus.Closed, stored.Status);
            Assert.NotNull(stored.ClosedAt);

            var notified = await db.Notifications.AsNoTracking()
                .Where(m => m.ReviewId == own.Id && m.Type == NotificationType.Closed)
                .ToListAsync();
            Assert.Equal(2, notified.Count);
            Assert.Contains(notified, m => m.UserId == context.Submitter.Id);
            Assert.Contains(notified, m => m.UserId == context.ReviewerA.Id);
        }

        await source.CreateBranchAsync("feature2", 1);
        await FetchMirrorAsync(factory, context.Repo);
        var other = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id], headBranch: "feature2");

        var admin = await factory.LoginAsAdminAsync();
        var adminClose = await CloseAsync(admin, other.Id, null);
        var adminBody = await adminClose.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.Equal(HttpStatusCode.OK, adminClose.StatusCode);
        Assert.NotNull(adminBody);
        Assert.Equal("CLOSED", adminBody.Status);
    }

    /// <summary>
    /// 通过后再补加审核人，评审单回到 OPEN，必须等新审核人通过才会再次 APPROVED。
    /// </summary>
    [Fact]
    public async Task Added_reviewer_must_approve_before_approved_status()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var approved = await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var approvedBody = await approved.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(approvedBody);
        Assert.Equal("APPROVED", approvedBody.Status);

        var added = await AddReviewersAsync(submitter, review.Id, [context.ReviewerB.Id]);
        var addedBody = await added.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.NotNull(addedBody);
        Assert.Equal("OPEN", addedBody.Status);
        Assert.Equal(
            "PENDING",
            Assert.Single(addedBody.Reviewers, m => m.UserId == context.ReviewerB.Id).State);

        var repeatedApproval = await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var stillOpen = await repeatedApproval.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(stillOpen);
        Assert.Equal("OPEN", stillOpen.Status);

        var final = await DecideAsync(reviewerB, review.Id, "APPROVED", null);
        var finalBody = await final.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(finalBody);
        Assert.Equal("APPROVED", finalBody.Status);
    }

    /// <summary>
    /// 移除尚未给出结论的审核人不会触发重算，不能借此自动通过。
    /// </summary>
    [Fact]
    public async Task Removing_a_pending_reviewer_does_not_auto_approve()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        var approved = await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var removed = await RemoveReviewerAsync(submitter, review.Id, context.ReviewerB.Id);
        var body = await removed.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("OPEN", body.Status);
        Assert.Equal(
            "REMOVED",
            Assert.Single(body.Reviewers, m => m.UserId == context.ReviewerB.Id).State);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notified = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id
                && m.UserId == context.Submitter.Id
                && m.Type == NotificationType.ReviewerRemoved)
            .ToListAsync();

        // 提交人自己移除审核人时不通知自己
        Assert.Empty(notified);
    }

    /// <summary>
    /// 移除唯一一名仍在册的审核人返回 400 LAST_REVIEWER，且不持久化任何修改。
    /// </summary>
    [Fact]
    public async Task Removing_the_last_active_reviewer_is_rejected()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var response = await RemoveReviewerAsync(submitter, review.Id, context.ReviewerA.Id);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("LAST_REVIEWER", body.Code);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.ReviewReviewers.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.UserId == context.ReviewerA.Id);
        Assert.Equal(ReviewerState.Pending, stored.State);
        Assert.DoesNotContain(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == review.Id)
                .ToListAsync(),
            m => m.Action == "REVIEWER_REMOVED");
    }

    /// <summary>
    /// 管理员可以移除已给出结论的审核人，移除后按剩余审核人重算状态。
    /// </summary>
    [Fact]
    public async Task Admin_can_remove_approved_reviewer_and_status_is_recomputed()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var reviewerC = factory.SeedUser(UniqueName("reviewer-c"), TestPassword);
        SeedMember(factory, context.Repo.Id, reviewerC.Id, isMerger: false);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var approved = await DecideAsync(reviewerB, review.Id, "APPROVED", null);
        var approvedBody = await approved.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(approvedBody);
        Assert.Equal("APPROVED", approvedBody.Status);

        // 补人把状态拉回 OPEN，再移除待审人；此时仍在册的审核人全部通过但状态按 P25 保持 OPEN
        await AddReviewersAsync(submitter, review.Id, [reviewerC.Id]);
        var afterRemovePending = await RemoveReviewerAsync(submitter, review.Id, reviewerC.Id);
        var pendingBody = await afterRemovePending.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(pendingBody);
        Assert.Equal("OPEN", pendingBody.Status);

        var admin = await factory.LoginAsAdminAsync();
        var removed = await RemoveReviewerAsync(admin, review.Id, context.ReviewerA.Id);
        var removedBody = await removed.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.NotNull(removedBody);
        Assert.Equal("APPROVED", removedBody.Status);
        Assert.Equal(
            "REMOVED",
            Assert.Single(removedBody.Reviewers, m => m.UserId == context.ReviewerA.Id).State);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(
            await db.AuditLogs.AsNoTracking()
                .Where(m => m.TargetId == review.Id && m.ActorId == factory.AdminUserId)
                .ToListAsync(),
            m => m.Action == "REVIEWER_REMOVED");
    }

    /// <summary>
    /// 已被移除的审核人不参与状态重算。
    /// </summary>
    [Fact]
    public async Task Removed_reviewer_is_ignored_by_recompute()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var removed = await RemoveReviewerAsync(submitter, review.Id, context.ReviewerB.Id);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var approved = await DecideAsync(reviewerA, review.Id, "APPROVED", null);
        var body = await approved.Content.ReadFromJsonAsync<ReviewDetailBody>();
        Assert.NotNull(body);
        Assert.Equal("APPROVED", body.Status);
    }

    /// <summary>
    /// 两个审核人并发通过时，合并人与提交人各只收到一条通过通知。
    /// </summary>
    [Fact]
    public async Task Concurrent_approvals_notify_each_recipient_once()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerA = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        var responses = await Task.WhenAll(
            DecideAsync(reviewerA, review.Id, "APPROVED", null),
            DecideAsync(reviewerB, review.Id, "APPROVED", null));

        Assert.All(responses, m => Assert.Equal(HttpStatusCode.OK, m.StatusCode));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Reviews.AsNoTracking().SingleAsync(m => m.Id == review.Id);
        Assert.Equal(ReviewStatus.Approved, stored.Status);

        var approved = await db.Notifications.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.Type == NotificationType.Approved)
            .ToListAsync();
        Assert.Equal(2, approved.Count);
        Assert.Single(approved, m => m.UserId == context.Merger.Id);
        Assert.Single(approved, m => m.UserId == context.Submitter.Id);
    }

    /// <summary>
    /// 以过期期望状态重试状态迁移时不得再报告变化，避免重复通知。
    /// </summary>
    [Fact]
    public async Task Retried_status_transition_reports_no_change()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewer = await factory.LoginAsync(context.ReviewerA.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);
        await DecideAsync(reviewer, review.Id, "APPROVED", null);

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ReviewService>();
            var retried = await service.ApplyStatusAsync(
                review.Id, ReviewStatus.Open, ReviewStatus.Approved, CancellationToken.None);
            Assert.Equal(ReviewStatus.Approved, retried.Status);
            Assert.False(retried.Changed);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Reviews
                .Where(m => m.Id == review.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(r => r.Status, ReviewStatus.Open));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ReviewService>();
            var applied = await service.ApplyStatusAsync(
                review.Id, ReviewStatus.Open, ReviewStatus.Approved, CancellationToken.None);
            Assert.Equal(ReviewStatus.Approved, applied.Status);
            Assert.True(applied.Changed);
        }
    }

    /// <summary>
    /// 两个管理员并发移除仅剩的两名审核人时只有一个成功，另一个返回 400 LAST_REVIEWER。
    /// </summary>
    [Fact]
    public async Task Concurrent_removals_leave_one_active_reviewer()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);
        var adminOne = await factory.LoginAsAdminAsync();
        var adminTwo = await factory.LoginAsAdminAsync();

        var responses = await Task.WhenAll(
            RemoveReviewerAsync(adminOne, review.Id, context.ReviewerA.Id),
            RemoveReviewerAsync(adminTwo, review.Id, context.ReviewerB.Id));

        Assert.Single(responses, m => m.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(
            responses, m => m.StatusCode == HttpStatusCode.BadRequest);
        var body = await rejected.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Equal("LAST_REVIEWER", body.Code);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeCount = await db.ReviewReviewers.AsNoTracking()
            .CountAsync(m => m.ReviewId == review.Id && m.State != ReviewerState.Removed);
        Assert.Equal(1, activeCount);
    }

    /// <summary>
    /// 已被移除的审核人不能再提交结论，条件更新也不会把 Removed 写回。
    /// </summary>
    [Fact]
    public async Task Decision_does_not_revive_removed_reviewer()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var reviewerB = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var admin = await factory.LoginAsAdminAsync();
        var review = await CreateReviewAsync(
            submitter, context.Repo.Id, [context.ReviewerA.Id, context.ReviewerB.Id]);

        var removed = await RemoveReviewerAsync(admin, review.Id, context.ReviewerB.Id);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var decision = await DecideAsync(reviewerB, review.Id, "APPROVED", null);
        await AssertErrorAsync(decision, HttpStatusCode.Forbidden, "NOT_A_REVIEWER");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionId = await db.Reviews.AsNoTracking()
            .Where(m => m.Id == review.Id)
            .Select(m => m.CurrentRevisionId)
            .SingleAsync();
        var service = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var affected = await service.TryUpdateReviewerDecisionAsync(
            review.Id,
            context.ReviewerB.Id,
            ReviewerState.Approved,
            null,
            revisionId ?? Guid.Empty,
            CancellationToken.None);

        Assert.Equal(0, affected);
        var stored = await db.ReviewReviewers.AsNoTracking()
            .SingleAsync(m => m.ReviewId == review.Id && m.UserId == context.ReviewerB.Id);
        Assert.Equal(ReviewerState.Removed, stored.State);
        Assert.DoesNotContain(
            await db.ReviewDecisions.AsNoTracking()
                .Where(m => m.ReviewId == review.Id && m.UserId == context.ReviewerB.Id)
                .ToListAsync(),
            m => m.State == ReviewerState.Approved);
    }

    /// <summary>
    /// 终态评审单不再暴露审核人管理入口，增删审核人返回 409。
    /// </summary>
    [Fact]
    public async Task Terminal_review_hides_reviewer_management()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        var closed = await CloseAsync(submitter, review.Id, "结束");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var closedDetail = await submitter.GetFromJsonAsync<ReviewDetailBody>(
            $"/api/reviews/{review.Id}");
        Assert.NotNull(closedDetail);
        Assert.False(closedDetail.Permissions.CanManageReviewers);
        await AssertErrorAsync(
            await AddReviewersAsync(submitter, review.Id, [context.ReviewerB.Id]),
            HttpStatusCode.Conflict,
            "REVIEW_NOT_ACTIVE");
        await AssertErrorAsync(
            await RemoveReviewerAsync(submitter, review.Id, context.ReviewerA.Id),
            HttpStatusCode.Conflict,
            "REVIEW_NOT_ACTIVE");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Reviews
                .Where(m => m.Id == review.Id)
                .ExecuteUpdateAsync(m => m.SetProperty(r => r.Status, ReviewStatus.Merged));
        }

        var mergedDetail = await submitter.GetFromJsonAsync<ReviewDetailBody>(
            $"/api/reviews/{review.Id}");
        Assert.NotNull(mergedDetail);
        Assert.False(mergedDetail.Permissions.CanManageReviewers);
        await AssertErrorAsync(
            await AddReviewersAsync(submitter, review.Id, [context.ReviewerB.Id]),
            HttpStatusCode.Conflict,
            "REVIEW_NOT_ACTIVE");
        await AssertErrorAsync(
            await RemoveReviewerAsync(submitter, review.Id, context.ReviewerA.Id),
            HttpStatusCode.Conflict,
            "REVIEW_NOT_ACTIVE");
    }

    /// <summary>
    /// 另一个仓库的成员对评审单的所有写操作都按不存在处理，返回 404。
    /// </summary>
    [Fact]
    public async Task Non_members_get_404_for_reviewer_actions()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var outsider = factory.SeedUser(UniqueName("outsider"), TestPassword);
        SeedForeignRepoMembership(factory, source.Url, outsider.Id);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);
        var outsiderClient = await factory.LoginAsync(outsider.Username, TestPassword);

        await AssertErrorAsync(
            await DecideAsync(outsiderClient, review.Id, "APPROVED", null),
            HttpStatusCode.NotFound,
            "NOT_FOUND");
        await AssertErrorAsync(
            await CloseAsync(outsiderClient, review.Id, null),
            HttpStatusCode.NotFound,
            "NOT_FOUND");
        await AssertErrorAsync(
            await AddReviewersAsync(outsiderClient, review.Id, [context.ReviewerB.Id]),
            HttpStatusCode.NotFound,
            "NOT_FOUND");
        await AssertErrorAsync(
            await RemoveReviewerAsync(outsiderClient, review.Id, context.ReviewerA.Id),
            HttpStatusCode.NotFound,
            "NOT_FOUND");
    }

    /// <summary>
    /// 同仓库但没有对应角色的成员仍然返回 403。
    /// </summary>
    [Fact]
    public async Task Members_without_role_get_403()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var context = await SeedContextAsync(factory, source);
        var submitter = await factory.LoginAsync(context.Submitter.Username, TestPassword);
        var member = await factory.LoginAsync(context.ReviewerB.Username, TestPassword);
        var review = await CreateReviewAsync(submitter, context.Repo.Id, [context.ReviewerA.Id]);

        await AssertErrorAsync(
            await DecideAsync(member, review.Id, "APPROVED", null),
            HttpStatusCode.Forbidden,
            "NOT_A_REVIEWER");
        await AssertErrorAsync(
            await CloseAsync(member, review.Id, null),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
        await AssertErrorAsync(
            await AddReviewersAsync(member, review.Id, [context.ReviewerB.Id]),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
        await AssertErrorAsync(
            await RemoveReviewerAsync(member, review.Id, context.ReviewerA.Id),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    /// <summary>
    /// 准备仓库、提交人、两名审核人、合并人以及镜像中的 feature 分支。
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
        var merger = factory.SeedUser(UniqueName("merger"), TestPassword);
        var repo = factory.SeedRepo(source.Url, UniqueName("review"));
        SeedMember(factory, repo.Id, submitter.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerA.Id, isMerger: false);
        SeedMember(factory, repo.Id, reviewerB.Id, isMerger: false);
        SeedMember(factory, repo.Id, merger.Id, isMerger: true);
        await source.CreateBranchAsync(HeadBranch, 2);
        await FetchMirrorAsync(factory, repo);
        return new ReviewContext(source, repo, submitter, reviewerA, reviewerB, merger);
    }

    /// <summary>
    /// 直接把提交人写成审核人，用于验证提交人不能给出结论。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="submitterId">提交人主键</param>
    private static void SeedSubmitterAsReviewer(
        TestAppFactory factory,
        Guid reviewId,
        Guid submitterId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionId = db.Reviews.AsNoTracking()
            .Where(m => m.Id == reviewId)
            .Select(m => m.CurrentRevisionId)
            .Single();
        db.ReviewReviewers.Add(new ReviewReviewer
        {
            Id = Guid.NewGuid(),
            ReviewId = reviewId,
            UserId = submitterId,
            State = ReviewerState.Pending,
            StateRevisionId = revisionId
        });
        db.SaveChanges();
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
    /// 把用户加入另一个仓库，用于验证跨仓库访问按不存在处理。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="gitUrl">仓库地址</param>
    /// <param name="userId">用户主键</param>
    private static void SeedForeignRepoMembership(
        TestAppFactory factory,
        string gitUrl,
        Guid userId)
    {
        var repo = factory.SeedRepo(gitUrl, UniqueName("other-repo"));
        SeedMember(factory, repo.Id, userId, isMerger: false);
    }

    /// <summary>
    /// 断言错误响应状态码与业务错误码。
    /// </summary>
    /// <param name="response">HTTP 响应</param>
    /// <param name="status">期望状态码</param>
    /// <param name="code">期望业务错误码</param>
    /// <returns></returns>
    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal(status, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(code, body.Code);
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
    /// 创建评审单并返回详情。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <param name="headBranch">提交分支</param>
    /// <returns>评审单详情</returns>
    private static async Task<ReviewDetailBody> CreateReviewAsync(
        HttpClient client,
        Guid repoId,
        IReadOnlyList<Guid> reviewerIds,
        string headBranch = HeadBranch)
    {
        var response = await client.PostJsonWithCsrfAsync(
            "/api/reviews",
            new
            {
                repoId,
                headBranch,
                baseBranch = BaseBranch,
                title = "状态机测试",
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
    /// 关闭评审单。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="reason">关闭原因</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> CloseAsync(
        HttpClient client,
        Guid reviewId,
        string? reason)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/close",
            new { reason });
    }

    /// <summary>
    /// 新增审核人。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userIds">待新增的审核人</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> AddReviewersAsync(
        HttpClient client,
        Guid reviewId,
        IReadOnlyList<Guid> userIds)
    {
        return client.PostJsonWithCsrfAsync(
            $"/api/reviews/{reviewId}/reviewers",
            new { userIds });
    }

    /// <summary>
    /// 移除审核人。
    /// </summary>
    /// <param name="client">已登录的客户端</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">审核人主键</param>
    /// <returns>HTTP 响应</returns>
    private static Task<HttpResponseMessage> RemoveReviewerAsync(
        HttpClient client,
        Guid reviewId,
        Guid userId)
    {
        return client.DeleteWithCsrfAsync($"/api/reviews/{reviewId}/reviewers/{userId}");
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
    /// <param name="Merger">合并人</param>
    private sealed record ReviewContext(
        TestGitRepo Source,
        Repo Repo,
        User Submitter,
        User ReviewerA,
        User ReviewerB,
        User Merger);
}
