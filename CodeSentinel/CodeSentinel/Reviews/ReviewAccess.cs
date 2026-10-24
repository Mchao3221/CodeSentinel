using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单访问控制：非仓库成员且非管理员一律按不存在处理，避免暴露评审单存在性。
/// </summary>
internal static class ReviewAccess
{
    /// <summary>
    /// 读取评审单并校验仓库访问权，无权访问时返回 404。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单</returns>
    public static async Task<Review> LoadForMemberAsync(
        AppDbContext db,
        Guid reviewId,
        Guid userId,
        CancellationToken ct)
    {
        var review = await db.Reviews.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == reviewId, ct)
            ?? throw NotFound();
        var isAdmin = await db.Users.AsNoTracking()
            .AnyAsync(m => m.Id == userId && m.IsAdmin, ct);
        if (isAdmin)
        {
            return review;
        }

        var isMember = await db.RepoMembers.AsNoTracking()
            .AnyAsync(m => m.RepoId == review.RepoId && m.UserId == userId, ct);
        return isMember ? review : throw NotFound();
    }

    /// <summary>
    /// 校验评审单处于允许操作的状态。
    /// </summary>
    /// <param name="status">评审单状态</param>
    public static void EnsureActive(ReviewStatus status)
    {
        if (status is not (ReviewStatus.Open
            or ReviewStatus.ChangesRequested
            or ReviewStatus.Approved))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "REVIEW_NOT_ACTIVE",
                "评审单当前状态不允许该操作");
        }
    }

    /// <summary>
    /// 构造“评审单不存在”异常，越权时不暴露评审单是否存在。
    /// </summary>
    /// <returns>业务异常</returns>
    public static ApiException NotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "评审单不存在");
    }
}
