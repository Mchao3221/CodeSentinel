using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单服务：创建评审单、锁定版本 SHA 与组装详情。
/// </summary>
public sealed class ReviewService(
    AppDbContext db,
    MirrorService mirrorService,
    AuditService auditService,
    NotificationService notificationService,
    ReviewReviewerService reviewerService)
{
    /// <summary>
    /// 创建评审单审计动作。
    /// </summary>
    public const string ReviewCreatedAction = "REVIEW_CREATED";

    /// <summary>
    /// 审核人提交结论审计动作。
    /// </summary>
    public const string ReviewDecidedAction = "REVIEW_DECIDED";

    /// <summary>
    /// 关闭评审单审计动作。
    /// </summary>
    public const string ReviewClosedAction = "REVIEW_CLOSED";

    private const string ReviewTargetType = "REVIEW";
    private const int MaxTitleLength = 200;
    private const int MaxNoteLength = 2000;
    private const int MaxCloseReasonLength = 500;
    private const string RemoteTrackingPrefix = "refs/remotes/origin/";
    private const string HeadRefPrefix = "refs/heads/";
    private const string ShortRemotePrefix = "origin/";
    private const string AnyRefPrefix = "refs/";

    /// <summary>
    /// 创建评审单：校验仓库成员、分支与审核人，写入评审单、版本 1、审核人、审计，并通知审核人。
    /// </summary>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="request">创建请求</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的评审单</returns>
    public async Task<Review> CreateAsync(
        Guid submitterId,
        CreateReviewRequest request,
        CancellationToken ct)
    {
        var repo = await LoadRepoForSubmitterAsync(request.RepoId, submitterId, ct);
        var baseBranch = NormalizeBranch(request.BaseBranch);
        var headBranch = NormalizeBranch(request.HeadBranch);
        var (baseSha, headSha) = await LockShasAsync(repo.Id, baseBranch, headBranch, ct);
        var reviewerIds = await NormalizeReviewersAsync(repo.Id, submitterId, request.ReviewerIds, ct);
        var title = NormalizeTitle(request.Title);
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        var review = BuildReview(
            repo.Id, submitterId, baseBranch, headBranch, baseSha, headSha,
            title, description, reviewerIds);

        auditService.Add(submitterId, ReviewCreatedAction, ReviewTargetType, review.Id, new
        {
            repoId = repo.Id,
            headBranch,
            baseBranch,
            baseSha,
            headSha,
            reviewerIds
        });

        await SaveCreatedAsync(review, ct);
        await notificationService.NotifyReviewersAsync(
            review.Id, NotificationType.ReviewSubmitted, ct);

        return review;
    }

    /// <summary>
    /// 读取评审单详情，并计算当前用户可执行的操作。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单详情</returns>
    public async Task<ReviewDetailDto> GetDetailAsync(
        Guid reviewId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var review = await LoadAccessibleReviewAsync(reviewId, userId, isAdmin, ct);
        return await BuildDetailAsync(review, userId, isAdmin, ct);
    }

    /// <summary>
    /// 审核人提交结论：写入审核人状态与结论历史，重算评审单状态，状态变化时发送通知并记审计。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">给出结论的用户主键</param>
    /// <param name="state">结论状态</param>
    /// <param name="note">结论附言</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task DecideAsync(
        Guid reviewId,
        Guid userId,
        ReviewerState state,
        string? note,
        CancellationToken ct)
    {
        EnsureValidDecisionState(state);
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, userId, ct);
        ReviewAccess.EnsureActive(review.Status);
        await EnsureAssignedReviewerAsync(reviewId, userId, ct);
        EnsureNotSubmitter(review, userId);
        var normalizedNote = NormalizeNote(state, note);
        var revisionId = review.CurrentRevisionId ?? Guid.Empty;

        var affected = await TryUpdateReviewerDecisionAsync(
            reviewId, userId, state, normalizedNote, revisionId, ct);
        if (affected == 0)
        {
            throw NotAReviewer();
        }

        AddDecisionHistory(review, userId, state, normalizedNote, revisionId);
        await db.SaveChangesAsync(ct);

        var next = ReviewStateMachine.Recompute(
            await LoadActiveReviewerStatesAsync(reviewId, ct));
        var (finalStatus, changed) = await ApplyStatusAsync(reviewId, review.Status, next, ct);
        await NotifyDecisionAsync(review, changed, finalStatus, state, userId, ct);
    }

    /// <summary>
    /// 关闭评审单：校验权限，写入关闭状态、时间与原因，并通知提交人与审核人。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">操作人主键</param>
    /// <param name="reason">关闭原因</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task CloseAsync(
        Guid reviewId,
        Guid userId,
        string? reason,
        CancellationToken ct)
    {
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, userId, ct);
        ReviewAccess.EnsureActive(review.Status);
        await EnsureCanCloseAsync(review, userId, ct);
        var normalizedReason = NormalizeCloseReason(reason);
        var now = DateTime.UtcNow;

        if (!await TryCloseAsync(reviewId, review.Status, normalizedReason, now, ct))
        {
            await RetryCloseAsync(reviewId, normalizedReason, now, ct);
        }

        await auditService.RecordAsync(
            userId,
            ReviewClosedAction,
            ReviewTargetType,
            reviewId,
            new { reason = normalizedReason },
            ct);

        var recipients = await LoadActiveReviewerIdsAsync(reviewId, ct);
        recipients.Add(review.SubmitterId);
        await notificationService.NotifyUsersAsync(reviewId, recipients, NotificationType.Closed, ct);
    }

    /// <summary>
    /// 新增审核人：委托审核人服务写入，随后按最新审核人结论重算评审单状态。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="userIds">待新增的审核人主键列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task AddReviewersAsync(
        Guid reviewId,
        Guid actorId,
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        var expectedStatus = await reviewerService.AddAsync(reviewId, actorId, userIds, ct);

        var next = ReviewStateMachine.Recompute(
            await LoadActiveReviewerStatesAsync(reviewId, ct));
        await ApplyStatusAsync(reviewId, expectedStatus, next, ct);
    }

    /// <summary>
    /// 移除审核人：委托审核人服务软删除，必要时重算评审单状态；非提交人操作时通知提交人。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="userId">被移除的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task RemoveReviewerAsync(
        Guid reviewId,
        Guid actorId,
        Guid userId,
        CancellationToken ct)
    {
        var result = await reviewerService.RemoveAsync(reviewId, actorId, userId, ct);

        if (result.ShouldRecompute)
        {
            var next = ReviewStateMachine.Recompute(
                await LoadActiveReviewerStatesAsync(reviewId, ct));
            await ApplyStatusAsync(reviewId, result.ExpectedStatus, next, ct);
        }

        // 提交人自己移除审核人时不通知自己，管理员代为移除时才提醒提交人补人
        if (actorId == result.SubmitterId)
        {
            return;
        }

        await notificationService.NotifyReviewerRemovedAsync(
            reviewId, result.SubmitterId, userId, "REMOVED_BY_ADMIN", ct);
    }

    /// <summary>
    /// 校验结论状态只允许通过、要求修改与撤回三种。
    /// </summary>
    /// <param name="state">结论状态</param>
    private static void EnsureValidDecisionState(ReviewerState state)
    {
        if (state is not (ReviewerState.Approved
            or ReviewerState.ChangesRequested
            or ReviewerState.Pending))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_STATE",
                "结论只支持 APPROVED、CHANGES_REQUESTED、PENDING");
        }
    }

    /// <summary>
    /// 读取评审单，不存在时返回 404。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单</returns>
    private async Task<Review> LoadReviewAsync(Guid reviewId, CancellationToken ct)
    {
        return await db.Reviews.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reviewId, ct)
            ?? throw ReviewNotFound();
    }

    /// <summary>
    /// 校验当前用户是该评审单上未移除的审核人，否则返回 403。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task EnsureAssignedReviewerAsync(
        Guid reviewId,
        Guid userId,
        CancellationToken ct)
    {
        var assigned = await db.ReviewReviewers.AsNoTracking()
            .AnyAsync(m => m.ReviewId == reviewId
                && m.UserId == userId
                && m.State != ReviewerState.Removed, ct);

        if (!assigned)
        {
            throw NotAReviewer();
        }
    }

    /// <summary>
    /// 构造“不是被分配的审核人”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException NotAReviewer()
    {
        return new ApiException(
            StatusCodes.Status403Forbidden,
            "NOT_A_REVIEWER",
            "只有被分配的审核人可以给出结论");
    }

    /// <summary>
    /// 提交人不能对自己的评审单给出结论。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">用户主键</param>
    private static void EnsureNotSubmitter(Review review, Guid userId)
    {
        if (review.SubmitterId == userId)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "SUBMITTER_CANNOT_DECIDE",
                "提交人不能对自己的评审单给出结论");
        }
    }

    /// <summary>
    /// 归一化结论附言：要求修改时必填，其余情况空串视为无附言。
    /// </summary>
    /// <param name="state">结论状态</param>
    /// <param name="note">原始附言</param>
    /// <returns>归一化后的附言</returns>
    private static string? NormalizeNote(ReviewerState state, string? note)
    {
        var trimmed = note?.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (state == ReviewerState.ChangesRequested && normalized is null)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "NOTE_REQUIRED",
                "要求修改时必须填写说明");
        }

        if (normalized is not null && normalized.Length > MaxNoteLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "NOTE_TOO_LONG",
                $"结论附言不能超过 {MaxNoteLength} 个字符");
        }

        return normalized;
    }

    /// <summary>
    /// 用带“未移除”谓词的条件更新写入审核人结论，避免把并发移除的行复活。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="state">结论状态</param>
    /// <param name="note">归一化后的附言</param>
    /// <param name="revisionId">结论对应的评审版本</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>受影响行数，0 表示审核人已被移除</returns>
    internal async Task<int> TryUpdateReviewerDecisionAsync(
        Guid reviewId,
        Guid userId,
        ReviewerState state,
        string? note,
        Guid revisionId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.ReviewReviewers
            .Where(m => m.ReviewId == reviewId
                && m.UserId == userId
                && m.State != ReviewerState.Removed)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.State, state)
                .SetProperty(r => r.Note, note)
                .SetProperty(r => r.StateRevisionId, revisionId)
                .SetProperty(r => r.UpdatedAt, now), ct);
    }

    /// <summary>
    /// 追加一条结论历史与审计，由调用方统一保存。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">用户主键</param>
    /// <param name="state">结论状态</param>
    /// <param name="note">归一化后的附言</param>
    /// <param name="revisionId">结论对应的评审版本</param>
    private void AddDecisionHistory(
        Review review,
        Guid userId,
        ReviewerState state,
        string? note,
        Guid revisionId)
    {
        db.ReviewDecisions.Add(new ReviewDecision
        {
            Id = Guid.NewGuid(),
            ReviewId = review.Id,
            RevisionId = revisionId,
            UserId = userId,
            State = state,
            Note = note
        });

        auditService.Add(userId, ReviewDecidedAction, ReviewTargetType, review.Id, new
        {
            state = state.ToString(),
            hasNote = note is not null,
            revisionId
        });
    }

    /// <summary>
    /// 读取评审单上未移除审核人的结论状态。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>结论状态列表</returns>
    private async Task<List<ReviewerState>> LoadActiveReviewerStatesAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed)
            .Select(m => m.State)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 读取评审单上未移除审核人的主键。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>审核人主键列表</returns>
    private async Task<List<Guid>> LoadActiveReviewerIdsAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// 乐观更新评审单状态：仅当当前状态等于期望值时更新，冲突时重算并重试一次。
    /// 返回值中的 Changed 表示本次请求是否真正完成了状态迁移，用于避免重复通知。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="expected">期望的当前状态</param>
    /// <param name="next">目标状态</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>最终生效的状态，以及是否由本次请求完成迁移</returns>
    internal async Task<(ReviewStatus Status, bool Changed)> ApplyStatusAsync(
        Guid reviewId,
        ReviewStatus expected,
        ReviewStatus next,
        CancellationToken ct)
    {
        if (next == expected)
        {
            return (expected, false);
        }

        var now = DateTime.UtcNow;
        if (await TryUpdateStatusAsync(reviewId, expected, next, now, ct))
        {
            return (next, true);
        }

        var latest = await LoadReviewAsync(reviewId, ct);
        ReviewAccess.EnsureActive(latest.Status);
        var retryNext = ReviewStateMachine.Recompute(
            await LoadActiveReviewerStatesAsync(reviewId, ct));
        var changed = retryNext != latest.Status;

        if (changed && !await TryUpdateStatusAsync(reviewId, latest.Status, retryNext, now, ct))
        {
            throw ConcurrentUpdate();
        }

        return (retryNext, changed);
    }

    /// <summary>
    /// 按乐观条件更新评审单状态与通过时间。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="expected">期望的当前状态</param>
    /// <param name="next">目标状态</param>
    /// <param name="now">本次更新时间（UTC）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否更新成功</returns>
    private async Task<bool> TryUpdateStatusAsync(
        Guid reviewId,
        ReviewStatus expected,
        ReviewStatus next,
        DateTime now,
        CancellationToken ct)
    {
        var approvedAt = next == ReviewStatus.Approved ? now : (DateTime?)null;
        var affected = await db.Reviews
            .Where(m => m.Id == reviewId && m.Status == expected)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.Status, next)
                .SetProperty(r => r.ApprovedAt, approvedAt)
                .SetProperty(r => r.MergeHint, false)
                .SetProperty(r => r.MergeHintCount, 0)
                .SetProperty(r => r.UpdatedAt, now), ct);

        return affected > 0;
    }

    /// <summary>
    /// 关闭评审单的乐观更新，冲突时读取最新状态重试一次。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="reason">关闭原因</param>
    /// <param name="now">关闭时间（UTC）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task RetryCloseAsync(
        Guid reviewId,
        string? reason,
        DateTime now,
        CancellationToken ct)
    {
        var latest = await LoadReviewAsync(reviewId, ct);
        ReviewAccess.EnsureActive(latest.Status);

        if (!await TryCloseAsync(reviewId, latest.Status, reason, now, ct))
        {
            throw ConcurrentUpdate();
        }
    }

    /// <summary>
    /// 按乐观条件把评审单置为已关闭，并写入关闭时间与原因。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="expected">期望的当前状态</param>
    /// <param name="reason">关闭原因</param>
    /// <param name="now">关闭时间（UTC）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否更新成功</returns>
    private async Task<bool> TryCloseAsync(
        Guid reviewId,
        ReviewStatus expected,
        string? reason,
        DateTime now,
        CancellationToken ct)
    {
        var affected = await db.Reviews
            .Where(m => m.Id == reviewId && m.Status == expected)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.Status, ReviewStatus.Closed)
                .SetProperty(r => r.ClosedAt, now)
                .SetProperty(r => r.CloseReason, reason)
                .SetProperty(r => r.UpdatedAt, now), ct);

        return affected > 0;
    }

    /// <summary>
    /// 状态迁移由本次请求完成时按结论类型通知：通过通知合并人与提交人，要求修改通知提交人，撤回通知提交人。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="changed">本次请求是否真正完成状态迁移</param>
    /// <param name="finalStatus">操作后状态</param>
    /// <param name="state">本次结论</param>
    /// <param name="actorId">提交结论的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task NotifyDecisionAsync(
        Review review,
        bool changed,
        ReviewStatus finalStatus,
        ReviewerState state,
        Guid actorId,
        CancellationToken ct)
    {
        if (!changed)
        {
            return;
        }

        if (finalStatus == ReviewStatus.Approved)
        {
            var recipients = await LoadMergerIdsAsync(review.RepoId, ct);
            recipients.Add(review.SubmitterId);
            await notificationService.NotifyUsersAsync(
                review.Id, recipients, NotificationType.Approved, ct);
            return;
        }

        if (finalStatus == ReviewStatus.ChangesRequested)
        {
            await notificationService.NotifyUsersAsync(
                review.Id, [review.SubmitterId], NotificationType.ChangesRequested, ct);
            return;
        }

        // 剩余分支是“撤回”：只有撤回让状态回退时才通知提交人
        if (state != ReviewerState.Pending)
        {
            return;
        }

        await notificationService.NotifyDecisionWithdrawnAsync(
            review.Id, review.SubmitterId, actorId, ct);
    }

    /// <summary>
    /// 读取仓库的全部合并人。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>合并人主键列表</returns>
    private async Task<List<Guid>> LoadMergerIdsAsync(Guid repoId, CancellationToken ct)
    {
        return await db.RepoMembers.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.IsMerger)
            .Select(m => m.UserId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 校验当前用户可以关闭评审单：提交人、合并人或管理员。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task EnsureCanCloseAsync(Review review, Guid userId, CancellationToken ct)
    {
        var allowed = review.SubmitterId == userId
            || await IsAdminUserAsync(userId, ct)
            || await IsMergerAsync(review.RepoId, userId, ct);

        if (!allowed)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "无权关闭该评审单");
        }
    }

    /// <summary>
    /// 判断用户是否为管理员。
    /// </summary>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否管理员</returns>
    private async Task<bool> IsAdminUserAsync(Guid userId, CancellationToken ct)
    {
        return await db.Users.AsNoTracking()
            .AnyAsync(m => m.Id == userId && m.IsAdmin, ct);
    }

    /// <summary>
    /// 归一化关闭原因，空串视为未填写。
    /// </summary>
    /// <param name="reason">原始关闭原因</param>
    /// <returns>归一化后的关闭原因</returns>
    private static string? NormalizeCloseReason(string? reason)
    {
        var trimmed = reason?.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (normalized is not null && normalized.Length > MaxCloseReasonLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_REASON",
                $"关闭原因不能超过 {MaxCloseReasonLength} 个字符");
        }

        return normalized;
    }

    /// <summary>
    /// 构造“并发更新冲突”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException ConcurrentUpdate()
    {
        return new ApiException(
            StatusCodes.Status409Conflict,
            "CONCURRENT_UPDATE",
            "评审单已被其他操作修改，请刷新后重试");
    }

    /// <summary>
    /// 读取提交人所属仓库，非成员与不存在都返回 404。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>仓库</returns>
    private async Task<Repo> LoadRepoForSubmitterAsync(
        Guid repoId,
        Guid submitterId,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking()
            .Where(m => m.Id == repoId
                && db.RepoMembers.Any(member => member.RepoId == m.Id && member.UserId == submitterId))
            .FirstOrDefaultAsync(ct);

        return repo ?? throw RepoNotFound();
    }

    /// <summary>
    /// 锁定两侧分支的 head SHA，并校验提交分支领先目标分支。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="baseBranch">目标分支</param>
    /// <param name="headBranch">提交分支</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>基线提交与评审提交</returns>
    private async Task<(string BaseSha, string HeadSha)> LockShasAsync(
        Guid repoId,
        string baseBranch,
        string headBranch,
        CancellationToken ct)
    {
        var baseSha = await mirrorService.GetHeadAsync(repoId, baseBranch, ct)
            ?? throw BranchNotFound(baseBranch);
        var headSha = await mirrorService.GetHeadAsync(repoId, headBranch, ct)
            ?? throw BranchNotFound(headBranch);

        if (!await mirrorService.IsAncestorAsync(repoId, baseSha, headSha, ct))
        {
            throw NoChanges();
        }

        var aheadBy = await mirrorService.CountAheadAsync(repoId, baseSha, headSha, ct);
        if (aheadBy <= 0)
        {
            throw NoChanges();
        }

        return (baseSha, headSha);
    }

    /// <summary>
    /// 校验审核人非空、不含提交人，且全部是启用中的仓库成员。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="reviewerIds">请求中的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>去重后的审核人主键</returns>
    private async Task<IReadOnlyList<Guid>> NormalizeReviewersAsync(
        Guid repoId,
        Guid submitterId,
        IReadOnlyList<Guid>? reviewerIds,
        CancellationToken ct)
    {
        var ids = reviewerIds?
            .Where(m => m != Guid.Empty)
            .Distinct()
            .ToList() ?? [];

        if (ids.Count == 0)
        {
            throw InvalidReviewer("评审单至少需要一名审核人");
        }

        if (ids.Contains(submitterId))
        {
            throw InvalidReviewer("提交人不能作为评审人");
        }

        var memberCount = await db.RepoMembers.AsNoTracking()
            .Where(m => m.RepoId == repoId
                && ids.Contains(m.UserId)
                && db.Users.Any(user => user.Id == m.UserId && user.IsActive))
            .CountAsync(ct);

        if (memberCount != ids.Count)
        {
            throw InvalidReviewer("审核人必须是启用中的仓库成员");
        }

        return ids;
    }

    /// <summary>
    /// 构造评审单、版本 1 与待审核的审核人实体并加入工作单元。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="submitterId">提交人主键</param>
    /// <param name="baseBranch">目标分支</param>
    /// <param name="headBranch">提交分支</param>
    /// <param name="baseSha">基线提交</param>
    /// <param name="headSha">评审提交</param>
    /// <param name="title">标题</param>
    /// <param name="description">评审说明</param>
    /// <param name="reviewerIds">审核人主键</param>
    /// <returns>创建的评审单</returns>
    private Review BuildReview(
        Guid repoId,
        Guid submitterId,
        string baseBranch,
        string headBranch,
        string baseSha,
        string headSha,
        string title,
        string? description,
        IReadOnlyList<Guid> reviewerIds)
    {
        var reviewId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        var review = new Review
        {
            Id = reviewId,
            RepoId = repoId,
            BaseBranch = baseBranch,
            HeadBranch = headBranch,
            SubmitterId = submitterId,
            Title = title,
            Description = description,
            Status = ReviewStatus.Open,
            CurrentRevisionId = revisionId,
            BaseSha = baseSha,
            HeadSha = headSha
        };

        db.Reviews.Add(review);
        db.ReviewRevisions.Add(new ReviewRevision
        {
            Id = revisionId,
            ReviewId = reviewId,
            Seq = 1,
            BaseSha = baseSha,
            HeadSha = headSha
        });

        foreach (var reviewerId in reviewerIds)
        {
            db.ReviewReviewers.Add(new ReviewReviewer
            {
                Id = Guid.NewGuid(),
                ReviewId = reviewId,
                UserId = reviewerId,
                State = ReviewerState.Pending,
                StateRevisionId = revisionId
            });
        }

        return review;
    }

    /// <summary>
    /// 一次保存评审单、版本、审核人与审计；唯一索引冲突时翻译为带回已有主键的 409。
    /// </summary>
    /// <param name="review">待保存的评审单</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task SaveCreatedAsync(Review review, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var existingId = await db.Reviews.AsNoTracking()
                .Where(m => m.RepoId == review.RepoId
                    && m.HeadBranch == review.HeadBranch
                    && (m.Status == ReviewStatus.Open
                        || m.Status == ReviewStatus.ChangesRequested
                        || m.Status == ReviewStatus.Approved))
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);

            throw new ActiveReviewConflictException(
                existingId ?? Guid.Empty,
                "该分支已存在进行中的评审单");
        }
    }

    /// <summary>
    /// 读取评审单并校验当前用户是否可访问其仓库。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单</returns>
    private async Task<Review> LoadAccessibleReviewAsync(
        Guid reviewId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reviewId, ct)
            ?? throw ReviewNotFound();

        var allowed = isAdmin || await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == review.RepoId && m.UserId == userId, ct);

        return allowed ? review : throw ReviewNotFound();
    }

    /// <summary>
    /// 组装评审单详情，包括版本、审核人与当前用户权限。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单详情</returns>
    private async Task<ReviewDetailDto> BuildDetailAsync(
        Review review,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking()
            .Where(m => m.Id == review.RepoId)
            .Select(m => new ReviewRepoRef(m.Id, m.Name))
            .FirstAsync(ct);
        var submitter = await db.Users.AsNoTracking()
            .Where(m => m.Id == review.SubmitterId)
            .Select(m => new ReviewUserRef(m.Id, m.DisplayName))
            .FirstAsync(ct);
        var isMerger = await IsMergerAsync(review.RepoId, userId, ct);
        var revisions = await LoadRevisionsAsync(review.Id, ct);
        var reviewerRows = await LoadReviewerRowsAsync(review.Id, ct);
        var currentRevision = revisions.FirstOrDefault(m => m.Id == review.CurrentRevisionId)?.Seq ?? 0;
        var permissions = BuildPermissions(review, userId, isAdmin, isMerger, reviewerRows);

        return new ReviewDetailDto(
            review.Id,
            repo,
            review.BaseBranch,
            review.HeadBranch,
            submitter,
            review.Title,
            review.Description,
            review.Status,
            review.BaseSha,
            review.HeadSha,
            currentRevision,
            revisions,
            reviewerRows.Select(m => new ReviewReviewerDto(m.UserId, m.DisplayName, m.State, m.Note)).ToList(),
            permissions,
            review.MergeHint,
            review.MergedAt,
            review.MergeCommitSha,
            review.CloseReason,
            review.CreatedAt,
            review.UpdatedAt);
    }

    /// <summary>
    /// 读取评审单的全部版本，按版本号升序。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>版本列表</returns>
    private async Task<IReadOnlyList<ReviewRevisionDto>> LoadRevisionsAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await db.ReviewRevisions.AsNoTracking()
            .Where(m => m.ReviewId == reviewId)
            .OrderBy(m => m.Seq)
            .Select(m => new ReviewRevisionDto(m.Id, m.Seq, m.HeadSha, m.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 读取评审单审核人及显示名，按加入顺序排列。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>审核人列表</returns>
    private async Task<IReadOnlyList<ReviewerRow>> LoadReviewerRowsAsync(
        Guid reviewId,
        CancellationToken ct)
    {
        return await (
            from reviewer in db.ReviewReviewers.AsNoTracking()
            join user in db.Users.AsNoTracking() on reviewer.UserId equals user.Id
            where reviewer.ReviewId == reviewId
            orderby reviewer.CreatedAt, reviewer.UserId
            select new ReviewerRow(
                reviewer.UserId,
                user.DisplayName,
                reviewer.State,
                reviewer.StateRevisionId,
                reviewer.Note))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 计算当前用户对评审单的操作权限。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="isMerger">当前用户是否为仓库合并人</param>
    /// <param name="reviewers">审核人列表</param>
    /// <returns>操作权限</returns>
    private static ReviewPermissionsDto BuildPermissions(
        Review review,
        Guid userId,
        bool isAdmin,
        bool isMerger,
        IReadOnlyList<ReviewerRow> reviewers)
    {
        var assignment = reviewers.FirstOrDefault(
            m => m.UserId == userId && m.State != ReviewerState.Removed);
        var active = review.Status is ReviewStatus.Open
            or ReviewStatus.ChangesRequested
            or ReviewStatus.Approved;
        var canDecide = active
            && assignment is not null
            && assignment.StateRevisionId == review.CurrentRevisionId;
        var canManageReviewers = active && (review.SubmitterId == userId || isAdmin);

        return new ReviewPermissionsDto(
            canDecide,
            canManageReviewers,
            review.SubmitterId == userId || isMerger || isAdmin,
            review.Status == ReviewStatus.Approved && (isMerger || isAdmin));
    }

    /// <summary>
    /// 判断用户是否为仓库合并人。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否合并人</returns>
    private async Task<bool> IsMergerAsync(Guid repoId, Guid userId, CancellationToken ct)
    {
        return await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == repoId && m.UserId == userId && m.IsMerger, ct);
    }

    /// <summary>
    /// 校验并归一化分支名：远端跟踪引用、origin 前缀与 heads 引用统一成短名，其它引用直接拒绝。
    /// </summary>
    /// <param name="branch">原始分支名</param>
    /// <returns>短分支名</returns>
    private static string NormalizeBranch(string? branch)
    {
        var value = branch?.Trim() ?? string.Empty;

        // 逐层剥离已知前缀，避免 refs/remotes/origin/refs/heads/x 这类嵌套写法绕过归一化
        while (TryStripKnownPrefix(ref value))
        {
        }

        if (value.Length == 0 || value.StartsWith(AnyRefPrefix, StringComparison.Ordinal))
        {
            throw BranchNotFound(value);
        }

        return value;
    }

    /// <summary>
    /// 剥离一层已知的分支引用前缀。
    /// </summary>
    /// <param name="value">待归一化的分支名</param>
    /// <returns>是否发生了剥离</returns>
    private static bool TryStripKnownPrefix(ref string value)
    {
        if (value.StartsWith(RemoteTrackingPrefix, StringComparison.Ordinal))
        {
            value = value[RemoteTrackingPrefix.Length..];
            return true;
        }

        if (value.StartsWith(HeadRefPrefix, StringComparison.Ordinal))
        {
            value = value[HeadRefPrefix.Length..];
            return true;
        }

        if (value.StartsWith(ShortRemotePrefix, StringComparison.Ordinal))
        {
            value = value[ShortRemotePrefix.Length..];
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验并归一化标题。
    /// </summary>
    /// <param name="title">原始标题</param>
    /// <returns>去空白后的标题</returns>
    private static string NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxTitleLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_TITLE",
                $"标题不能为空且不超过 {MaxTitleLength} 个字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 判断异常是否为 SQLite 唯一约束冲突。
    /// </summary>
    /// <param name="exception">EF 更新异常</param>
    /// <returns>是否为唯一约束冲突</returns>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException { SqliteErrorCode: 19 };
    }

    /// <summary>
    /// 构造“仓库不存在”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException RepoNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "仓库不存在");
    }

    /// <summary>
    /// 构造“评审单不存在”异常，越权时不暴露评审单是否存在。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException ReviewNotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "评审单不存在");
    }

    /// <summary>
    /// 构造“分支不存在”异常。
    /// </summary>
    /// <param name="branch">分支名</param>
    /// <returns>业务异常</returns>
    private static ApiException BranchNotFound(string branch)
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "BRANCH_NOT_FOUND",
            string.IsNullOrEmpty(branch) ? "分支名不能为空" : $"镜像中不存在分支：{branch}");
    }

    /// <summary>
    /// 构造“没有可评审提交”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException NoChanges()
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "NO_CHANGES",
            "提交分支没有领先目标分支的提交");
    }

    /// <summary>
    /// 构造“审核人不合法”异常。
    /// </summary>
    /// <param name="message">错误信息</param>
    /// <returns>业务异常</returns>
    private static ApiException InvalidReviewer(string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, "INVALID_REVIEWER", message);
    }

    /// <summary>
    /// 审核人明细行，供权限计算与详情组装使用。
    /// </summary>
    /// <param name="UserId">审核人主键</param>
    /// <param name="DisplayName">显示名</param>
    /// <param name="State">结论状态</param>
    /// <param name="StateRevisionId">结论对应版本</param>
    /// <param name="Note">结论附言</param>
    private sealed record ReviewerRow(
        Guid UserId,
        string DisplayName,
        ReviewerState State,
        Guid? StateRevisionId,
        string? Note);
}

/// <summary>
/// 活跃评审单唯一约束冲突，由端点转换成带回已有评审单主键的 409 响应。
/// </summary>
public sealed class ActiveReviewConflictException : Exception
{
    /// <summary>
    /// 已存在的活跃评审单主键。
    /// </summary>
    public Guid ReviewId { get; }

    /// <summary>
    /// 初始化活跃评审单冲突异常。
    /// </summary>
    /// <param name="reviewId">已存在的活跃评审单主键</param>
    /// <param name="message">错误信息</param>
    public ActiveReviewConflictException(Guid reviewId, string message) : base(message)
    {
        ReviewId = reviewId;
    }
}
