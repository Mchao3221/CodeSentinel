using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Repos;

/// <summary>
/// 分支查询端点：分支列表与“未开单分支”，只读取本地镜像，不访问远端。
/// </summary>
public static class BranchEndpoints
{
    /// <summary>
    /// 映射分支查询端点，要求登录且具备仓库访问权限。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapBranchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/repos/{id:guid}").RequireAuthorization();

        group.MapGet("/branches", ListBranchesAsync);
        group.MapGet("/branches/unreviewed", ListUnreviewedAsync);
    }

    /// <summary>
    /// 列出镜像中的远端分支，q 按分支名前缀过滤（区分大小写）。
    /// 分支列表规模很小，按设计不提供分页。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="q">分支名前缀，可选</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分支列表</returns>
    private static async Task<IResult> ListBranchesAsync(
        Guid id,
        string? q,
        AppDbContext db,
        HttpContext httpContext,
        MirrorService mirrorService,
        CancellationToken ct)
    {
        var repo = await RepoAccess.EnsureAsync(db, httpContext, id, ct);

        // 镜像尚未建立时返回空列表，不能在这里创建空目录
        if (!mirrorService.IsMirrorReady(repo.Id))
        {
            return Results.Ok(new BranchListResponse([]));
        }

        var branches = await mirrorService.ListBranchesAsync(repo.Id, ct);
        var prefix = q?.Trim();
        var items = string.IsNullOrEmpty(prefix)
            ? branches
            : branches
                .Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

        return Results.Ok(new BranchListResponse(
            items.Select(m => new BranchDto(m.Name, m.HeadSha)).ToList()));
    }

    /// <summary>
    /// 列出未开单分支：领先默认目标分支且没有活跃评审单。
    /// 默认目标分支不在镜像中时返回空列表，不能返回 500。
    /// </summary>
    /// <param name="id">仓库主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>未开单分支列表，按领先提交数降序</returns>
    private static async Task<IResult> ListUnreviewedAsync(
        Guid id,
        AppDbContext db,
        HttpContext httpContext,
        MirrorService mirrorService,
        CancellationToken ct)
    {
        var repo = await RepoAccess.EnsureAsync(db, httpContext, id, ct);

        if (!mirrorService.IsMirrorReady(repo.Id))
        {
            return Results.Ok(new UnreviewedBranchListResponse([]));
        }

        var baseHead = await mirrorService.GetHeadAsync(repo.Id, repo.DefaultBaseBranch, ct);
        if (baseHead is null)
        {
            return Results.Ok(new UnreviewedBranchListResponse([]));
        }

        var branches = await mirrorService.ListBranchesAsync(repo.Id, ct);
        var reviewedBranches = await LoadActiveReviewBranchesAsync(db, repo.Id, ct);
        var items = await CollectUnreviewedAsync(
            mirrorService, repo, branches, baseHead, reviewedBranches, ct);

        var sorted = items
            .OrderByDescending(m => m.AheadBy)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        return Results.Ok(new UnreviewedBranchListResponse(sorted));
    }

    /// <summary>
    /// 逐个分支判断是否领先默认分支，并统计领先的提交数。
    /// </summary>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="repo">仓库</param>
    /// <param name="branches">镜像中的全部分支</param>
    /// <param name="baseHead">默认目标分支 head</param>
    /// <param name="reviewedBranches">已有活跃评审单的分支集合</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>未开单分支列表</returns>
    private static async Task<List<UnreviewedBranchDto>> CollectUnreviewedAsync(
        MirrorService mirrorService,
        Repo repo,
        IReadOnlyList<BranchInfo> branches,
        string baseHead,
        HashSet<string> reviewedBranches,
        CancellationToken ct)
    {
        var items = new List<UnreviewedBranchDto>();

        foreach (var branch in branches)
        {
            if (string.Equals(branch.Name, repo.DefaultBaseBranch, StringComparison.Ordinal))
            {
                continue;
            }

            if (reviewedBranches.Contains(branch.Name))
            {
                continue;
            }

            if (string.Equals(branch.HeadSha, baseHead, StringComparison.Ordinal))
            {
                continue;
            }

            if (!await mirrorService.IsAncestorAsync(repo.Id, baseHead, branch.HeadSha, ct))
            {
                continue;
            }

            var aheadBy = await mirrorService.CountAheadAsync(repo.Id, baseHead, branch.HeadSha, ct);
            if (aheadBy <= 0)
            {
                continue;
            }

            items.Add(new UnreviewedBranchDto(branch.Name, branch.HeadSha, aheadBy));
        }

        return items;
    }

    /// <summary>
    /// 读取存在活跃评审单（Open / ChangesRequested / Approved）的分支名集合。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分支名集合，区分大小写</returns>
    private static async Task<HashSet<string>> LoadActiveReviewBranchesAsync(
        AppDbContext db,
        Guid repoId,
        CancellationToken ct)
    {
        var names = await db.Reviews.AsNoTracking()
            .Where(m => m.RepoId == repoId
                && (m.Status == ReviewStatus.Open
                    || m.Status == ReviewStatus.ChangesRequested
                    || m.Status == ReviewStatus.Approved))
            .Select(m => m.HeadBranch)
            .ToListAsync(ct);

        return names.ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// 分支项响应。
/// </summary>
/// <param name="Name">分支名</param>
/// <param name="HeadSha">分支 head 提交</param>
public sealed record BranchDto(string Name, string HeadSha);

/// <summary>
/// 分支列表响应。
/// </summary>
/// <param name="Items">分支列表</param>
public sealed record BranchListResponse(IReadOnlyList<BranchDto> Items);

/// <summary>
/// 未开单分支项响应。
/// </summary>
/// <param name="Name">分支名</param>
/// <param name="HeadSha">分支 head 提交</param>
/// <param name="AheadBy">领先默认分支的提交数</param>
public sealed record UnreviewedBranchDto(string Name, string HeadSha, int AheadBy);

/// <summary>
/// 未开单分支列表响应。
/// </summary>
/// <param name="Items">分支列表</param>
public sealed record UnreviewedBranchListResponse(IReadOnlyList<UnreviewedBranchDto> Items);
