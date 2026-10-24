using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Repos;

/// <summary>
/// 仓库级访问控制：管理员或仓库成员可访问，其他情况统一返回 404。
/// </summary>
public static class RepoAccess
{
    /// <summary>
    /// 加载仓库并校验当前用户的访问权限。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>只读加载的仓库</returns>
    public static async Task<Repo> EnsureAsync(
        AppDbContext db,
        HttpContext httpContext,
        Guid repoId,
        CancellationToken ct)
    {
        if (CurrentUser.IsAdmin(httpContext))
        {
            return await db.Repos.AsNoTracking().FirstOrDefaultAsync(m => m.Id == repoId, ct)
                ?? throw NotFound();
        }

        var userId = CurrentUser.RequireId(httpContext);
        var repo = await db.Repos.AsNoTracking()
            .Where(m => m.Id == repoId
                && db.RepoMembers.Any(member => member.RepoId == m.Id && member.UserId == userId))
            .FirstOrDefaultAsync(ct);

        return repo ?? throw NotFound();
    }

    /// <summary>
    /// 构造统一的“资源不存在”异常，越权时不暴露仓库是否存在。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException NotFound()
    {
        return new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "仓库不存在");
    }
}
