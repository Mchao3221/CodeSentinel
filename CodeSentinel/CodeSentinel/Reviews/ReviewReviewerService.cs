using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单审核人服务：新增与移除审核人，维护软删除、审计与并发保护。
/// </summary>
/// <param name="db">数据库上下文</param>
/// <param name="auditService">审计服务</param>
public sealed class ReviewReviewerService(AppDbContext db, AuditService auditService)
{
    /// <summary>
    /// 新增审核人审计动作。
    /// </summary>
    public const string ReviewersAddedAction = "REVIEWERS_ADDED";

    /// <summary>
    /// 移除审核人审计动作。
    /// </summary>
    public const string ReviewerRemovedAction = "REVIEWER_REMOVED";

    private const string ReviewTargetType = "REVIEW";

    /// <summary>
    /// 新增审核人：提交人或管理员可用；只接受启用中的非提交人仓库成员，已存在的人忽略。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="userIds">待新增的审核人主键列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作前的评审单状态</returns>
    public async Task<ReviewStatus> AddAsync(
        Guid reviewId,
        Guid actorId,
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, actorId, ct);
        ReviewAccess.EnsureActive(review.Status);
        await EnsureCanManageAsync(review, actorId, ct);
        var candidates = await ResolveNewReviewersAsync(review, userIds, ct);

        foreach (var userId in candidates)
        {
            db.ReviewReviewers.Add(new ReviewReviewer
            {
                Id = Guid.NewGuid(),
                ReviewId = reviewId,
                UserId = userId,
                State = ReviewerState.Pending,
                StateRevisionId = review.CurrentRevisionId
            });
        }

        auditService.Add(
            actorId,
            ReviewersAddedAction,
            ReviewTargetType,
            reviewId,
            new { userIds = candidates });
        await db.SaveChangesAsync(ct);

        return review.Status;
    }

    /// <summary>
    /// 移除审核人：置为 Removed 保留历史；提交人只能移除待审人，管理员可移除任何人。
    /// 事务内复核剩余在册人数，避免并发移除后没有审核人。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="userId">被移除的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作前状态、提交人主键与是否需要重算状态</returns>
    public async Task<(ReviewStatus ExpectedStatus, Guid SubmitterId, bool ShouldRecompute)> RemoveAsync(
        Guid reviewId,
        Guid actorId,
        Guid userId,
        CancellationToken ct)
    {
        var review = await ReviewAccess.LoadForMemberAsync(db, reviewId, actorId, ct);
        ReviewAccess.EnsureActive(review.Status);
        var isAdmin = await IsAdminAsync(actorId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var target = await LoadReviewerAsync(reviewId, userId, ct);
        EnsureCanRemove(review, target, actorId, isAdmin);
        var shouldRecompute = target.State is ReviewerState.Approved
            or ReviewerState.ChangesRequested;

        var affected = await MarkRemovedAsync(reviewId, userId, isAdmin, ct);
        if (affected == 0)
        {
            throw RemovalConflict(isAdmin);
        }

        await EnsureNotLastReviewerAsync(reviewId, ct);
        auditService.Add(
            actorId,
            ReviewerRemovedAction,
            ReviewTargetType,
            reviewId,
            new { userId });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return (review.Status, review.SubmitterId, shouldRecompute);
    }

    /// <summary>
    /// 校验当前用户可以管理审核人：提交人或管理员。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task EnsureCanManageAsync(Review review, Guid userId, CancellationToken ct)
    {
        var allowed = review.SubmitterId == userId || await IsAdminAsync(userId, ct);

        if (!allowed)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "无权管理审核人");
        }
    }

    /// <summary>
    /// 解析待新增的审核人：去重、排除已存在的人、校验非提交人与启用中的仓库成员。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="userIds">请求中的审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>实际新增的审核人主键</returns>
    private async Task<List<Guid>> ResolveNewReviewersAsync(
        Review review,
        IReadOnlyList<Guid>? userIds,
        CancellationToken ct)
    {
        var requested = userIds?
            .Where(m => m != Guid.Empty)
            .Distinct()
            .ToList() ?? [];

        if (requested.Count == 0)
        {
            throw InvalidReviewer("至少需要一名审核人");
        }

        if (requested.Contains(review.SubmitterId))
        {
            throw InvalidReviewer("提交人不能作为评审人");
        }

        var existing = await db.ReviewReviewers.AsNoTracking()
            .Where(m => m.ReviewId == review.Id && requested.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(ct);
        var candidates = requested.Except(existing).ToList();

        if (candidates.Count == 0)
        {
            throw InvalidReviewer("没有可新增的审核人");
        }

        var memberCount = await db.RepoMembers.AsNoTracking()
            .Where(m => m.RepoId == review.RepoId
                && candidates.Contains(m.UserId)
                && db.Users.Any(user => user.Id == m.UserId && user.IsActive))
            .CountAsync(ct);

        if (memberCount != candidates.Count)
        {
            throw InvalidReviewer("审核人必须是启用中的仓库成员");
        }

        return candidates;
    }

    /// <summary>
    /// 读取审核人记录，不存在或已移除时返回 404。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">审核人主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>审核人记录</returns>
    private async Task<ReviewReviewer> LoadReviewerAsync(
        Guid reviewId,
        Guid userId,
        CancellationToken ct)
    {
        var reviewer = await db.ReviewReviewers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ReviewId == reviewId && m.UserId == userId, ct);

        if (reviewer is null || reviewer.State == ReviewerState.Removed)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "NOT_FOUND",
                "审核人不在评审单上");
        }

        return reviewer;
    }

    /// <summary>
    /// 校验操作人可以移除目标审核人：管理员可移除任何人，提交人只能移除待审人。
    /// </summary>
    /// <param name="review">评审单</param>
    /// <param name="target">目标审核人</param>
    /// <param name="actorId">操作人主键</param>
    /// <param name="isAdmin">操作人是否管理员</param>
    private static void EnsureCanRemove(
        Review review,
        ReviewReviewer target,
        Guid actorId,
        bool isAdmin)
    {
        if (isAdmin)
        {
            return;
        }

        if (review.SubmitterId != actorId)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "无权管理审核人");
        }

        if (target.State != ReviewerState.Pending)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "CANNOT_REMOVE_DECIDED",
                "只能移除尚未给出结论的审核人");
        }
    }

    /// <summary>
    /// 用带状态的谓词把审核人软删除，避免覆盖并发写入的结论。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">审核人主键</param>
    /// <param name="isAdmin">操作人是否管理员</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>受影响行数</returns>
    private async Task<int> MarkRemovedAsync(
        Guid reviewId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var query = db.ReviewReviewers.Where(m => m.ReviewId == reviewId
            && m.UserId == userId
            && m.State != ReviewerState.Removed);
        if (!isAdmin)
        {
            query = query.Where(m => m.State == ReviewerState.Pending);
        }

        var now = DateTime.UtcNow;
        return await query.ExecuteUpdateAsync(m => m
            .SetProperty(r => r.State, ReviewerState.Removed)
            .SetProperty(r => r.UpdatedAt, now), ct);
    }

    /// <summary>
    /// 事务内复核：移除后仍必须有至少一名非 Removed 的审核人。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task EnsureNotLastReviewerAsync(Guid reviewId, CancellationToken ct)
    {
        var remaining = await db.ReviewReviewers.AsNoTracking()
            .CountAsync(m => m.ReviewId == reviewId && m.State != ReviewerState.Removed, ct);

        if (remaining == 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "LAST_REVIEWER",
                "评审单至少需要一名审核人");
        }
    }

    /// <summary>
    /// 判断用户是否为管理员。
    /// </summary>
    /// <param name="userId">用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否管理员</returns>
    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        return await db.Users.AsNoTracking()
            .AnyAsync(m => m.Id == userId && m.IsAdmin, ct);
    }

    /// <summary>
    /// 构造移除审核人时并发冲突的异常：提交人视为结论已变化，管理员视为并发更新。
    /// </summary>
    /// <param name="isAdmin">操作人是否管理员</param>
    /// <returns>业务异常</returns>
    private static ApiException RemovalConflict(bool isAdmin)
    {
        return isAdmin
            ? new ApiException(
                StatusCodes.Status409Conflict,
                "CONCURRENT_UPDATE",
                "评审单已被其他操作修改，请刷新后重试")
            : new ApiException(
                StatusCodes.Status403Forbidden,
                "CANNOT_REMOVE_DECIDED",
                "只能移除尚未给出结论的审核人");
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
}
