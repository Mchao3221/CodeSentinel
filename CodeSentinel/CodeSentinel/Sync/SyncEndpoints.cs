using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Git;
using CodeSentinel.Repos;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Sync;

/// <summary>
/// 手动同步端点：管理员或仓库成员触发一次镜像同步，同一仓库 10 秒内只执行一次。
/// </summary>
public static class SyncEndpoints
{
    /// <summary>
    /// 映射手动同步端点，要求登录且具备仓库访问权限。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/repos/{id:guid}/sync", SyncRepoAsync).RequireAuthorization();
    }

    /// <summary>
    /// 触发一次镜像同步：先确保镜像存在，再 fetch，成功后写回同步时间。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="credentialProtector">凭据解密器</param>
    /// <param name="throttle">进程内同步限流器</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>同步结果</returns>
    private static async Task<IResult> SyncRepoAsync(
        Guid id,
        AppDbContext db,
        HttpContext httpContext,
        MirrorService mirrorService,
        CredentialProtector credentialProtector,
        SyncThrottle throttle,
        CancellationToken ct)
    {
        var repo = await RepoAccess.EnsureAsync(db, httpContext, id, ct);

        // 同一仓库同时只允许一个 fetch：worker 正在同步时直接拒绝
        if (!await throttle.TryEnterAsync(repo.Id, ct))
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "SYNC_IN_PROGRESS",
                "仓库正在同步，请稍后再试");
        }

        try
        {
            if (!throttle.TryBeginWindow(repo.Id, DateTime.UtcNow))
            {
                throw new ApiException(
                    StatusCodes.Status429TooManyRequests,
                    "SYNC_THROTTLED",
                    $"同步过于频繁，请 {SyncThrottle.ThrottleSeconds} 秒后再试");
            }

            var credentials = RepoCredentials.Read(credentialProtector, repo);
            await mirrorService.EnsureAsync(repo.Id, repo.GitUrl, credentials, ct);
            await mirrorService.FetchAsync(repo.Id, repo.GitUrl, credentials, ct);
            await MarkSyncedAsync(db, repo.Id, ct);

            return Results.Ok(new { synced = true });
        }
        finally
        {
            throttle.Exit(repo.Id);
        }
    }

    /// <summary>
    /// 同步成功后写回最近同步时间并清空错误、失败次数与退避时间。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新任务</returns>
    private static async Task MarkSyncedAsync(AppDbContext db, Guid repoId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await db.Repos
            .Where(m => m.Id == repoId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LastFetchedAt, now)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.FailureCount, 0)
                    .SetProperty(m => m.NextAttemptAt, (DateTime?)null)
                    .SetProperty(m => m.UpdatedAt, now),
                ct);
    }
}
