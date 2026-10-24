using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.AspNetCore.Http;

namespace CodeSentinel.Reviews;

/// <summary>
/// diff 相关端点：文件列表、单文件 diff 与全文，全部要求仓库成员访问权限。
/// </summary>
public static class DiffEndpoints
{
    /// <summary>
    /// 文件列表返回条目上限，超出部分通过 Truncated 标记。
    /// </summary>
    private const int MaxFiles = 300;

    /// <summary>
    /// 映射 diff 端点，全部要求登录。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapDiffEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reviews").RequireAuthorization();

        group.MapGet("/{id:guid}/files", ListFilesAsync);
        group.MapGet("/{id:guid}/diff", GetDiffAsync);
        group.MapGet("/{id:guid}/blob", GetBlobAsync);
    }

    /// <summary>
    /// 读取版本文件列表，超过 300 条时截断并标记 Truncated。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件列表响应</returns>
    private static async Task<IResult> ListFilesAsync(
        Guid id,
        AppDbContext db,
        DiffService diffService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var review = await ReviewAccess.LoadForMemberAsync(db, id, userId, ct);

        var files = await diffService.GetFilesAsync(
            review.RepoId, review.BaseSha, review.HeadSha, ct);
        var total = files.Count;
        var items = files.Take(MaxFiles).ToList();

        return Results.Ok(new DiffFileListResponse(
            items, total, 0, total, total > MaxFiles));
    }

    /// <summary>
    /// 读取单个文件的 diff，路径必须属于当前版本的 diff。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件 diff</returns>
    private static async Task<IResult> GetDiffAsync(
        Guid id,
        string? path,
        AppDbContext db,
        DiffService diffService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var review = await ReviewAccess.LoadForMemberAsync(db, id, userId, ct);
        EnsurePath(path);

        var file = await diffService.GetFileDiffAsync(
            review.RepoId, review.BaseSha, review.HeadSha, path!, ct);
        return Results.Ok(file);
    }

    /// <summary>
    /// 读取文件在 OLD（合并基）或 NEW（head）一侧的全文。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="side">OLD 或 NEW</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务，用于解析合并基</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件全文</returns>
    private static async Task<IResult> GetBlobAsync(
        Guid id,
        string? path,
        string? side,
        AppDbContext db,
        DiffService diffService,
        MirrorService mirrorService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var review = await ReviewAccess.LoadForMemberAsync(db, id, userId, ct);
        EnsurePath(path);
        await diffService.EnsurePathInDiffAsync(review.RepoId, review.BaseSha, review.HeadSha, path!, ct);

        var commitSha = await ResolveBlobCommitAsync(mirrorService, review, side, ct);
        var blob = await diffService.GetBlobAsync(review.RepoId, commitSha, path!, ct);
        return Results.Ok(blob);
    }

    /// <summary>
    /// 校验 path 参数非空。
    /// </summary>
    /// <param name="path">文件路径</param>
    private static void EnsurePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PATH",
                "path 不能为空");
        }
    }

    /// <summary>
    /// 解析 blob 应读取的提交：OLD 取合并基，NEW 取 head。
    /// </summary>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="review">评审单</param>
    /// <param name="side">OLD 或 NEW</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>提交 SHA</returns>
    private static async Task<string> ResolveBlobCommitAsync(
        MirrorService mirrorService,
        Review review,
        string? side,
        CancellationToken ct)
    {
        if (string.Equals(side, "OLD", StringComparison.OrdinalIgnoreCase))
        {
            var mergeBase = await mirrorService.GetMergeBaseAsync(
                review.RepoId, review.BaseSha, review.HeadSha, ct);
            return mergeBase ?? review.BaseSha;
        }

        if (side is not null && !string.Equals(side, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_SIDE",
                "side 只支持 OLD 或 NEW");
        }

        return review.HeadSha;
    }
}

/// <summary>
/// 文件列表响应。
/// </summary>
/// <param name="Items">文件摘要列表（截断后）</param>
/// <param name="Total">真实文件总数</param>
/// <param name="ViewedCount">已查看文件数，Task 12 前占位为 0</param>
/// <param name="FileCount">文件总数，Task 12 前与 Total 一致</param>
/// <param name="Truncated">是否因超过上限被截断</param>
public sealed record DiffFileListResponse(
    IReadOnlyList<DiffFileSummary> Items,
    int Total,
    int ViewedCount,
    int FileCount,
    bool Truncated);
