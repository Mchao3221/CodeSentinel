using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Notifications;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 合并检测服务：同步后判定评审是否已合并、处理分支删除关闭，并支持手工标记合并。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="mirrorService">镜像服务，用于读取目标分支 head 与祖先关系</param>
/// <param name="auditService">审计服务</param>
/// <param name="notificationService">通知服务</param>
public sealed class MergeDetectionService(
    AppDbContext db,
    MirrorService mirrorService,
    AuditService auditService,
    NotificationService notificationService)
{
    /// <summary>
    /// 评审单合并审计动作。
    /// </summary>
    public const string ReviewMergedAction = "REVIEW_MERGED";

    /// <summary>
    /// 评审分支被删除时写入的关闭原因。
    /// </summary>
    public const string BranchDeletedCloseReason = "BRANCH_DELETED";

    private const string ReviewTargetType = "REVIEW";
    private const int MergeHintThreshold = 3;

    /// <summary>
    /// 检测单个评审单是否已合并：仅 APPROVED 参与检测，已合并的评审单不会重复处理。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>检测结果</returns>
    public async Task<MergeCheckResult> CheckAsync(Guid reviewId, CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == reviewId, ct);

        if (review is null || review.Status != ReviewStatus.Approved)
        {
            return Unchanged(review);
        }

        var baseHead = await mirrorService.GetHeadAsync(review.RepoId, review.BaseBranch, ct);
        if (baseHead is null)
        {
            return new MergeCheckResult(false, review.MergeCommitSha, review.MergeHint);
        }

        if (await mirrorService.IsAncestorAsync(review.RepoId, review.HeadSha, baseHead, ct))
        {
            var marked = await ApplyMergedAsync(review, baseHead, null, ct);
            return marked
                ? new MergeCheckResult(true, baseHead, false)
                : Unchanged(review);
        }

        var hint = await RecordMergeMissAsync(review.Id, ct);
        return new MergeCheckResult(false, null, hint);
    }

    /// <summary>
    /// 同步仓库下全部评审单的合并与分支删除状态；应在镜像 fetch 成功后调用。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task SyncReviewMergeStatesAsync(Guid repoId, CancellationToken ct)
    {
        var reviews = await db.Reviews.AsNoTracking()
            .Where(m => m.RepoId == repoId
                && (m.Status == ReviewStatus.Open
                    || m.Status == ReviewStatus.ChangesRequested
                    || m.Status == ReviewStatus.Approved))
            .Select(m => new { m.Id, m.Status, m.HeadBranch })
            .ToListAsync(ct);

        // 从未成功 fetch 的镜像不参与分支删除判定
        if (reviews.Count == 0 || !mirrorService.IsMirrorReady(repoId))
        {
            return;
        }

        var branches = await mirrorService.ListBranchesAsync(repoId, ct);
        var branchNames = branches
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var review in reviews)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // 先检测合并：分支已删除但已合并的评审单必须保持 MERGED
            if (review.Status == ReviewStatus.Approved
                && (await CheckAsync(review.Id, ct)).Merged)
            {
                continue;
            }

            if (!branchNames.Contains(review.HeadBranch))
            {
                await CloseForDeletedBranchAsync(review.Id, ct);
            }
        }
    }

    /// <summary>
    /// 手工标记评审单为已合并：要求仓库合并人或管理员，且评审单状态为 APPROVED。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="mergeCommitSha">合并提交，未提供时取目标分支 head</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    public async Task MarkMergedManuallyAsync(
        Guid reviewId,
        Guid actorId,
        string? mergeCommitSha,
        CancellationToken ct)
    {
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, actorId, ct);
        await EnsureCanMarkMergedAsync(review, actorId, ct);
        EnsureMergeableStatus(review.Status);
        var sha = await ResolveMergeCommitAsync(review, mergeCommitSha, ct);

        if (!await ApplyMergedAsync(review, sha, actorId, ct))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "REVIEW_NOT_APPROVED",
                "评审单状态已变化，只有已通过的评审单可以标记为合并");
        }
    }

    /// <summary>
    /// 把评审单置为已合并，写入合并信息、审计并通知提交人与审核人。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="mergeCommitSha">合并提交</param>
    /// <param name="actorId">操作人，系统检测时为空</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否完成了状态迁移</returns>
    private async Task<bool> ApplyMergedAsync(
        Review review,
        string? mergeCommitSha,
        Guid? actorId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var affected = await db.Reviews
            .Where(m => m.Id == review.Id && m.Status == ReviewStatus.Approved)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.Status, ReviewStatus.Merged)
                .SetProperty(r => r.MergedAt, now)
                .SetProperty(r => r.MergeCommitSha, mergeCommitSha)
                .SetProperty(r => r.MergeHint, false)
                .SetProperty(r => r.MergeHintCount, 0)
                .SetProperty(r => r.UpdatedAt, now), ct);

        if (affected == 0)
        {
            return false;
        }

        auditService.Add(actorId, ReviewMergedAction, ReviewTargetType, review.Id, new
        {
            mergeCommitSha,
            source = actorId is null ? "SYSTEM" : "MANUAL"
        });
        await db.SaveChangesAsync(ct);
        await NotifyAudienceAsync(review, NotificationType.Merged, ct);
        return true;
    }

    /// <summary>
    /// 累加一次未检测到合并的同步次数，达到阈值后置合并提示。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的合并提示状态</returns>
    private async Task<bool> RecordMergeMissAsync(Guid reviewId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Reviews
            .Where(m => m.Id == reviewId && m.Status == ReviewStatus.Approved)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.MergeHintCount, r => r.MergeHintCount + 1)
                .SetProperty(
                    r => r.MergeHint,
                    r => r.MergeHintCount + 1 >= MergeHintThreshold)
                .SetProperty(r => r.UpdatedAt, now), ct);

        return await db.Reviews.AsNoTracking()
            .Where(m => m.Id == reviewId)
            .Select(m => m.MergeHint)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 分支被删除时关闭活跃评审单，写入关闭原因、审计并通知提交人与审核人。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task CloseForDeletedBranchAsync(Guid reviewId, CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == reviewId, ct);
        if (review is null || !IsActive(review.Status))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var affected = await db.Reviews
            .Where(m => m.Id == reviewId && m.Status == review.Status)
            .ExecuteUpdateAsync(m => m
                .SetProperty(r => r.Status, ReviewStatus.Closed)
                .SetProperty(r => r.ClosedAt, now)
                .SetProperty(r => r.CloseReason, BranchDeletedCloseReason)
                .SetProperty(r => r.UpdatedAt, now), ct);

        if (affected == 0)
        {
            return;
        }

        auditService.Add(
            null,
            ReviewService.ReviewClosedAction,
            ReviewTargetType,
            reviewId,
            new { reason = BranchDeletedCloseReason });
        await db.SaveChangesAsync(ct);
        await NotifyAudienceAsync(review, NotificationType.Closed, ct);
    }

    /// <summary>
    /// 给提交人与全部未移除审核人各发一条通知，接收人自动去重。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="type">通知类型</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task NotifyAudienceAsync(
        Review review,
        NotificationType type,
        CancellationToken ct)
    {
        var recipients = await db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && m.State != ReviewerState.Removed)
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
        recipients.Add(review.SubmitterId);

        await notificationService.NotifyUsersAsync(review.Id, recipients, type, ct);
    }

    /// <summary>
    /// 校验操作人具备标记合并权限：仓库合并人或管理员。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task EnsureCanMarkMergedAsync(
        Review review,
        Guid actorId,
        CancellationToken ct)
    {
        var isAdmin = await db.Users.AsNoTracking()
            .AnyAsync(m => m.Id == actorId && m.IsAdmin, ct);
        if (isAdmin)
        {
            return;
        }

        var isMerger = await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == review.RepoId
                && m.UserId == actorId
                && m.IsMerger, ct);

        if (!isMerger)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "只有仓库合并人或管理员可以标记合并");
        }
    }

    /// <summary>
    /// 校验评审单状态允许手工标记：已合并或已关闭返回 REVIEW_NOT_ACTIVE，其余非通过状态返回 REVIEW_NOT_APPROVED。
    /// </summary>
    /// <param name="status">评审单状态</param>
    private static void EnsureMergeableStatus(ReviewStatus status)
    {
        if (status == ReviewStatus.Approved)
        {
            return;
        }

        if (status is ReviewStatus.Merged or ReviewStatus.Closed)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "REVIEW_NOT_ACTIVE",
                "评审单当前状态不允许该操作");
        }

        throw new ApiException(
            StatusCodes.Status409Conflict,
            "REVIEW_NOT_APPROVED",
            "只有已通过的评审单可以标记为合并");
    }

    /// <summary>
    /// 解析合并提交：优先使用请求值，否则取目标分支当前 head，可能为空。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="mergeCommitSha">请求中的合并提交</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>合并提交或空</returns>
    private async Task<string?> ResolveMergeCommitAsync(
        Review review,
        string? mergeCommitSha,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(mergeCommitSha))
        {
            return mergeCommitSha.Trim();
        }

        return await mirrorService.GetHeadAsync(review.RepoId, review.BaseBranch, ct);
    }

    /// <summary>
    /// 非 APPROVED 状态原样返回评审单已有的合并信息。
    /// </summary>
    /// <param name="review">评审单，可为空</param>
    /// <returns>未发生变化的结果</returns>
    private static MergeCheckResult Unchanged(Review? review)
    {
        return new MergeCheckResult(
            false,
            review?.MergeCommitSha,
            review?.MergeHint ?? false);
    }

    /// <summary>
    /// 判断评审单是否处于活跃状态。
    /// </summary>
    /// <param name="status">评审单状态</param>
    /// <returns>是否活跃</returns>
    private static bool IsActive(ReviewStatus status)
    {
        return status is ReviewStatus.Open
            or ReviewStatus.ChangesRequested
            or ReviewStatus.Approved;
    }
}

/// <summary>
/// 合并检测结果。
/// </summary>
/// <param name="Merged">本次是否检测到并完成合并</param>
/// <param name="MergeCommitSha">合并提交</param>
/// <param name="MergeHint">是否提示未检测到合并</param>
public sealed record MergeCheckResult(bool Merged, string? MergeCommitSha, bool MergeHint);
