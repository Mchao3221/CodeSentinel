using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单端点：列表、详情与创建。
/// </summary>
public static class ReviewsEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>
    /// 映射评审单端点，全部要求登录。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reviews").RequireAuthorization();

        group.MapGet(string.Empty, ListReviewsAsync);
        group.MapGet("/{id:guid}", GetReviewAsync);
        group.MapPost(string.Empty, CreateReviewAsync);
    }

    /// <summary>
    /// 分页查询评审单：管理员看到全部，普通用户只看到自己所属仓库。
    /// </summary>
    /// <param name="scope">范围：mine / to_review / repo，默认 mine</param>
    /// <param name="repoId">仓库主键，可选</param>
    /// <param name="status">状态，接受枚举名或下划线形式</param>
    /// <param name="page">页码，从 1 开始</param>
    /// <param name="pageSize">每页条数，默认 20，最大 100</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分页评审单列表</returns>
    private static async Task<IResult> ListReviewsAsync(
        string? scope,
        Guid? repoId,
        string? status,
        int? page,
        int? pageSize,
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var isAdmin = CurrentUser.IsAdmin(httpContext);
        var currentPage = page is > 0 ? page.Value : 1;
        var currentPageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = BuildListQuery(
            db, userId, isAdmin, ParseScope(scope), repoId, ParseStatus(status));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(m => m.UpdatedAt)
            .ThenBy(m => m.Id)
            .Skip((currentPage - 1) * currentPageSize)
            .Take(currentPageSize)
            .Select(m => new ReviewListItemDto(
                m.Id,
                new ReviewRepoRef(
                    m.RepoId,
                    db.Repos.Where(repo => repo.Id == m.RepoId).Select(repo => repo.Name).First()),
                m.Title,
                m.Status,
                m.BaseBranch,
                m.HeadBranch,
                new ReviewUserRef(
                    m.SubmitterId,
                    db.Users.Where(user => user.Id == m.SubmitterId)
                        .Select(user => user.DisplayName)
                        .First()),
                db.ReviewReviewers.Count(reviewer => reviewer.ReviewId == m.Id
                    && reviewer.State != ReviewerState.Removed),
                db.ReviewReviewers.Count(reviewer => reviewer.ReviewId == m.Id
                    && reviewer.State == ReviewerState.Approved),
                db.ReviewRevisions.Where(revision => revision.Id == m.CurrentRevisionId)
                    .Select(revision => revision.Seq)
                    .FirstOrDefault(),
                m.CreatedAt,
                m.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<ReviewListItemDto>(
            items, total, currentPage, currentPageSize));
    }

    /// <summary>
    /// 读取评审单详情，要求仓库成员或管理员身份。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="reviewService">评审单服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评审单详情</returns>
    private static async Task<IResult> GetReviewAsync(
        Guid id,
        ReviewService reviewService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var detail = await reviewService.GetDetailAsync(
            id,
            CurrentUser.RequireId(httpContext),
            CurrentUser.IsAdmin(httpContext),
            ct);

        return Results.Ok(detail);
    }

    /// <summary>
    /// 创建评审单；同一分支已有活跃评审单时返回 409 并带上已有主键。
    /// </summary>
    /// <param name="request">创建请求</param>
    /// <param name="reviewService">评审单服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="jsonOptions">应用的 HTTP JSON 配置</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的评审单详情</returns>
    private static async Task<IResult> CreateReviewAsync(
        CreateReviewRequest request,
        ReviewService reviewService,
        HttpContext httpContext,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);

        try
        {
            var review = await reviewService.CreateAsync(userId, request, ct);
            var detail = await reviewService.GetDetailAsync(
                review.Id, userId, CurrentUser.IsAdmin(httpContext), ct);
            return Results.Created($"/api/reviews/{review.Id}", detail);
        }
        catch (ActiveReviewConflictException ex)
        {
            var response = new ActiveReviewConflictResponse(
                "ACTIVE_REVIEW_EXISTS",
                ex.Message,
                httpContext.TraceIdentifier,
                ex.ReviewId);
            return Results.Json(
                response,
                jsonOptions.Value.SerializerOptions,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// 按范围、仓库与状态组装列表查询，非管理员先限制到所属仓库。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="isAdmin">当前用户是否管理员</param>
    /// <param name="scope">查询范围</param>
    /// <param name="repoId">仓库主键，可选</param>
    /// <param name="status">状态，可选</param>
    /// <returns>评审单查询</returns>
    private static IQueryable<Review> BuildListQuery(
        AppDbContext db,
        Guid userId,
        bool isAdmin,
        ReviewScope scope,
        Guid? repoId,
        ReviewStatus? status)
    {
        var query = db.Reviews.AsNoTracking();
        if (!isAdmin)
        {
            query = query.Where(m => db.RepoMembers.Any(
                member => member.RepoId == m.RepoId && member.UserId == userId));
        }

        query = scope switch
        {
            ReviewScope.Mine => query.Where(m => m.SubmitterId == userId),
            ReviewScope.ToReview => query.Where(m => db.ReviewReviewers.Any(
                reviewer => reviewer.ReviewId == m.Id
                    && reviewer.UserId == userId
                    && reviewer.State != ReviewerState.Removed)),
            _ => query
        };

        if (repoId is not null)
        {
            query = query.Where(m => m.RepoId == repoId.Value);
        }

        if (status is not null)
        {
            query = query.Where(m => m.Status == status.Value);
        }

        return query;
    }

    /// <summary>
    /// 解析 scope 参数，缺省为 mine。
    /// </summary>
    /// <param name="scope">原始 scope</param>
    /// <returns>查询范围</returns>
    private static ReviewScope ParseScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mine" => ReviewScope.Mine,
            "to_review" => ReviewScope.ToReview,
            "repo" => ReviewScope.Repo,
            _ => throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_SCOPE",
                "scope 只支持 mine、to_review、repo")
        };
    }

    /// <summary>
    /// 解析状态参数，接受枚举名、下划线与任意大小写。
    /// </summary>
    /// <param name="status">原始状态</param>
    /// <returns>状态枚举，未提供时为 null</returns>
    private static ReviewStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (Enum.TryParse<ReviewStatus>(normalized, true, out var parsed))
        {
            return parsed;
        }

        throw new ApiException(
            StatusCodes.Status400BadRequest,
            "INVALID_STATUS",
            "不支持的状态值");
    }

    /// <summary>
    /// 评审单列表查询范围。
    /// </summary>
    private enum ReviewScope
    {
        /// <summary>
        /// 我提交的。
        /// </summary>
        Mine,

        /// <summary>
        /// 待我评审的。
        /// </summary>
        ToReview,

        /// <summary>
        /// 我可访问仓库的全部评审单。
        /// </summary>
        Repo
    }
}
