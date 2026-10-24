using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Reviews;

/// <summary>
/// 逐文件“已看过”端点：按当前版本的 blob id 记录标记，内容变化后自动失效。
/// </summary>
public static class ViewedEndpoints
{
    /// <summary>
    /// 映射已看过端点，要求登录。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapViewedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/reviews")
            .RequireAuthorization()
            .MapPut("/{id:guid}/files/viewed", SetViewedAsync);
    }

    /// <summary>
    /// 标记或取消标记文件已看过：写入当前版本对应侧的 blob id，取消时删除该行。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="request">标记请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务，用于解析删除文件的合并基</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>标记后的状态</returns>
    private static async Task<IResult> SetViewedAsync(
        Guid id,
        SetViewedRequest request,
        AppDbContext db,
        DiffService diffService,
        MirrorService mirrorService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var review = await ReviewAccess.LoadForMemberAsync(db, id, userId, ct);
        var path = NormalizePath(request.Path);
        var revision = await LoadCurrentRevisionAsync(db, review, ct);
        var files = await diffService.GetFilesAsync(
            review.RepoId, revision.BaseSha, revision.HeadSha, ct);
        var file = files.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal))
            ?? throw FileNotInDiff();
        var row = await db.ReviewFileViews.FirstOrDefaultAsync(
            m => m.ReviewId == review.Id && m.UserId == userId && m.FilePath == path, ct);

        if (!request.Viewed)
        {
            await RemoveAsync(db, row, ct);
            return Results.Ok(new ViewedStateResponse(path, false));
        }

        var blobSha = await ResolveBlobShaAsync(
            diffService, mirrorService, review.RepoId, revision, file, ct);
        await UpsertAsync(db, row, review.Id, userId, path, blobSha, ct);
        return Results.Ok(new ViewedStateResponse(path, true));
    }

    /// <summary>
    /// 读取评审单当前版本，供文件列表与标记接口共用。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="review">评审单</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当前版本</returns>
    internal static async Task<ReviewRevision> LoadCurrentRevisionAsync(
        AppDbContext db,
        Review review,
        CancellationToken ct)
    {
        if (review.CurrentRevisionId is null)
        {
            throw ReviewAccess.NotFound();
        }

        return await db.ReviewRevisions.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == review.CurrentRevisionId.Value, ct)
            ?? throw ReviewAccess.NotFound();
    }

    /// <summary>
    /// 计算当前用户在当前版本中已看过的文件路径集合；blob 不一致的旧行视为未看过且不删除。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务，用于解析删除文件的合并基</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="revision">当前版本</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="files">当前版本文件列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>已看过的文件路径集合</returns>
    internal static async Task<IReadOnlySet<string>> ResolveViewedPathsAsync(
        AppDbContext db,
        DiffService diffService,
        MirrorService mirrorService,
        Guid repoId,
        ReviewRevision revision,
        Guid userId,
        IReadOnlyList<DiffFileSummary> files,
        CancellationToken ct)
    {
        var rows = await db.ReviewFileViews.AsNoTracking()
            .Where(m => m.ReviewId == revision.ReviewId && m.UserId == userId)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var filesByPath = files.ToDictionary(m => m.Path, StringComparer.Ordinal);
        var candidates = rows
            .Where(m => filesByPath.ContainsKey(m.FilePath))
            .Select(m => new ViewCandidate(filesByPath[m.FilePath], m.BlobSha))
            .ToList();
        var keys = await BuildCurrentKeysAsync(
            diffService, mirrorService, repoId, revision, candidates, ct);

        var viewed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (keys.TryGetValue(candidate.File.Path, out var blobSha)
                && string.Equals(blobSha, candidate.BlobSha, StringComparison.Ordinal))
            {
                viewed.Add(candidate.File.Path);
            }
        }

        return viewed;
    }

    /// <summary>
    /// 计算候选文件在当前位置的 blob id 映射：新侧取 head，删除文件取合并基旧侧。
    /// </summary>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="revision">当前版本</param>
    /// <param name="candidates">已看过行对应的候选文件</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当前路径到 blob id 的映射</returns>
    private static async Task<Dictionary<string, string>> BuildCurrentKeysAsync(
        DiffService diffService,
        MirrorService mirrorService,
        Guid repoId,
        ReviewRevision revision,
        IReadOnlyList<ViewCandidate> candidates,
        CancellationToken ct)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        await AddHeadSideKeysAsync(diffService, repoId, revision.HeadSha, candidates, keys, ct);
        await AddDeletedSideKeysAsync(
            diffService, mirrorService, repoId, revision, candidates, keys, ct);
        return keys;
    }

    /// <summary>
    /// 一次读取 head 的 blob 映射，填充非删除文件的当前 blob id。
    /// </summary>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="headSha">当前版本终点提交</param>
    /// <param name="candidates">候选文件</param>
    /// <param name="keys">待填充的映射</param>
    /// <param name="ct">取消令牌</param>
    private static async Task AddHeadSideKeysAsync(
        DiffService diffService,
        Guid repoId,
        string headSha,
        IReadOnlyList<ViewCandidate> candidates,
        Dictionary<string, string> keys,
        CancellationToken ct)
    {
        var paths = candidates
            .Where(m => m.File.Status != FileChangeStatus.Deleted)
            .Select(m => m.File.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var blobShas = await diffService.GetBlobShaMapAsync(repoId, headSha, ct);
        foreach (var path in paths)
        {
            if (blobShas.TryGetValue(path, out var blobSha))
            {
                keys[path] = blobSha;
            }
        }
    }

    /// <summary>
    /// 删除的文件按合并基（回退基线）的旧路径读取 blob 映射，确认其仍可标记已看过。
    /// </summary>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="revision">当前版本</param>
    /// <param name="candidates">候选文件</param>
    /// <param name="keys">待填充的映射</param>
    /// <param name="ct">取消令牌</param>
    private static async Task AddDeletedSideKeysAsync(
        DiffService diffService,
        MirrorService mirrorService,
        Guid repoId,
        ReviewRevision revision,
        IReadOnlyList<ViewCandidate> candidates,
        Dictionary<string, string> keys,
        CancellationToken ct)
    {
        var deleted = candidates
            .Where(m => m.File.Status == FileChangeStatus.Deleted)
            .ToList();
        if (deleted.Count == 0)
        {
            return;
        }

        var commitSha = await mirrorService.GetMergeBaseAsync(
            repoId, revision.BaseSha, revision.HeadSha, ct) ?? revision.BaseSha;
        var blobShas = await diffService.GetBlobShaMapAsync(repoId, commitSha, ct);
        foreach (var candidate in deleted)
        {
            var oldPath = candidate.File.OldPath ?? candidate.File.Path;
            if (blobShas.TryGetValue(oldPath, out var blobSha))
            {
                keys[candidate.File.Path] = blobSha;
            }
        }
    }

    /// <summary>
    /// 解析标记时应保存的 blob id：非删除文件取 head 新侧，删除文件取合并基旧侧。
    /// </summary>
    /// <param name="diffService">diff 引擎</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="revision">当前版本</param>
    /// <param name="file">文件摘要</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>blob id</returns>
    private static async Task<string> ResolveBlobShaAsync(
        DiffService diffService,
        MirrorService mirrorService,
        Guid repoId,
        ReviewRevision revision,
        DiffFileSummary file,
        CancellationToken ct)
    {
        if (file.Status != FileChangeStatus.Deleted)
        {
            return await diffService.GetBlobShaAsync(
                repoId, revision.HeadSha, file.Path, ct);
        }

        var commitSha = await mirrorService.GetMergeBaseAsync(
            repoId, revision.BaseSha, revision.HeadSha, ct) ?? revision.BaseSha;
        return await diffService.GetBlobShaAsync(
            repoId, commitSha, file.OldPath ?? file.Path, ct);
    }

    /// <summary>
    /// 新增或更新已看过行，写入当前 blob id 与标记时间。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="row">已存在的行，可空</param>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="userId">用户主键</param>
    /// <param name="path">文件路径</param>
    /// <param name="blobSha">blob id</param>
    /// <param name="ct">取消令牌</param>
    private static async Task UpsertAsync(
        AppDbContext db,
        ReviewFileView? row,
        Guid reviewId,
        Guid userId,
        string path,
        string blobSha,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (row is null)
        {
            db.ReviewFileViews.Add(new ReviewFileView
            {
                Id = Guid.NewGuid(),
                ReviewId = reviewId,
                UserId = userId,
                FilePath = path,
                BlobSha = blobSha,
                ViewedAt = now
            });
        }
        else
        {
            row.BlobSha = blobSha;
            row.ViewedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 删除已看过行，行不存在时直接返回，保证取消操作幂等。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="row">已存在的行，可空</param>
    /// <param name="ct">取消令牌</param>
    private static async Task RemoveAsync(
        AppDbContext db,
        ReviewFileView? row,
        CancellationToken ct)
    {
        if (row is null)
        {
            return;
        }

        db.ReviewFileViews.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 归一化请求中的文件路径，空值返回 400 INVALID_PATH。
    /// </summary>
    /// <param name="path">原始路径</param>
    /// <returns>去空白后的路径</returns>
    private static string NormalizePath(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PATH",
                "path 不能为空");
        }

        return trimmed;
    }

    /// <summary>
    /// 构造“文件不在本次 diff 中”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException FileNotInDiff()
    {
        return new ApiException(
            StatusCodes.Status404NotFound,
            "FILE_NOT_IN_DIFF",
            "文件不在本次 diff 中");
    }

    /// <summary>
    /// 已看过行对应的候选文件。
    /// </summary>
    /// <param name="File">当前版本的文件摘要</param>
    /// <param name="BlobSha">行中保存的 blob id</param>
    private sealed record ViewCandidate(DiffFileSummary File, string BlobSha);
}

/// <summary>
/// 标记已看过请求。
/// </summary>
/// <param name="Path">文件路径，重命名后传当前路径</param>
/// <param name="Viewed">true 标记已看过，false 取消标记</param>
public sealed record SetViewedRequest(string? Path, bool Viewed);

/// <summary>
/// 标记已看过响应。
/// </summary>
/// <param name="Path">文件路径</param>
/// <param name="Viewed">标记后的状态</param>
public sealed record ViewedStateResponse(string Path, bool Viewed);
