using CodeSentinel.Auth;
using CodeSentinel.Data.Entities;

namespace CodeSentinel.Reviews;

/// <summary>
/// 行级评论端点：列表、创建、编辑、删除与解决状态流转，全部要求登录与仓库成员访问权。
/// </summary>
public static class CommentsEndpoints
{
    /// <summary>
    /// 映射评论端点，全部要求登录。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapCommentsEndpoints(this IEndpointRouteBuilder app)
    {
        var reviews = app.MapGroup("/reviews").RequireAuthorization();
        reviews.MapGet("/{id:guid}/comments", ListCommentsAsync);
        reviews.MapPost("/{id:guid}/comments", CreateCommentAsync);

        var comments = app.MapGroup("/comments").RequireAuthorization();
        comments.MapPatch("/{id:guid}", UpdateCommentAsync);
        comments.MapDelete("/{id:guid}", DeleteCommentAsync);
        comments.MapPost("/{id:guid}/resolve", ResolveCommentAsync);
        comments.MapPost("/{id:guid}/unresolve", UnresolveCommentAsync);
    }

    /// <summary>
    /// 读取评审单上的全部评论（含已删除与已失效），按创建时间升序；该接口有意不分页。
    /// 传入 revisionId 时只返回该版本创建的评论。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="revisionId">历史版本主键，可空；省略时返回全部版本</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>评论列表响应</returns>
    private static async Task<IResult> ListCommentsAsync(
        Guid id,
        Guid? revisionId,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var items = await commentService.ListAsync(
            id, revisionId, CurrentUser.RequireId(httpContext), ct);
        return Results.Ok(new CommentListResponse(items));
    }

    /// <summary>
    /// 创建评论或回复，返回 201 与创建的评论。
    /// </summary>
    /// <param name="id">评审单主键</param>
    /// <param name="request">创建评论请求</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的评论</returns>
    private static async Task<IResult> CreateCommentAsync(
        Guid id,
        CreateCommentRequest request,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var comment = await commentService.CreateAsync(
            id, CurrentUser.RequireId(httpContext), request, ct);
        var dto = await commentService.BuildDtoAsync(comment, ct);

        return Results.Created($"/api/reviews/{id}/comments/{comment.Id}", dto);
    }

    /// <summary>
    /// 修改评论正文，仅作者可操作。
    /// </summary>
    /// <param name="id">评论主键</param>
    /// <param name="request">修改请求</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    private static async Task<IResult> UpdateCommentAsync(
        Guid id,
        UpdateCommentRequest request,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var comment = await commentService.UpdateBodyAsync(
            id, CurrentUser.RequireId(httpContext), request.Body, ct);

        return Results.Ok(await commentService.BuildDtoAsync(comment, ct));
    }

    /// <summary>
    /// 软删除评论，作者或管理员可操作。
    /// </summary>
    /// <param name="id">评论主键</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    private static async Task<IResult> DeleteCommentAsync(
        Guid id,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var comment = await commentService.DeleteAsync(
            id,
            CurrentUser.RequireId(httpContext),
            CurrentUser.IsAdmin(httpContext),
            ct);

        return Results.Ok(await commentService.BuildDtoAsync(comment, ct));
    }

    /// <summary>
    /// 把评论标记为已解决。
    /// </summary>
    /// <param name="id">评论主键</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    private static Task<IResult> ResolveCommentAsync(
        Guid id,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        return SetResolvedCoreAsync(id, commentService, httpContext, true, ct);
    }

    /// <summary>
    /// 取消评论的已解决标记。
    /// </summary>
    /// <param name="id">评论主键</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    private static Task<IResult> UnresolveCommentAsync(
        Guid id,
        CommentService commentService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        return SetResolvedCoreAsync(id, commentService, httpContext, false, ct);
    }

    /// <summary>
    /// 解决与取消解决的公共实现。
    /// </summary>
    /// <param name="id">评论主键</param>
    /// <param name="commentService">评论服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="resolve">true 表示解决</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的评论</returns>
    private static async Task<IResult> SetResolvedCoreAsync(
        Guid id,
        CommentService commentService,
        HttpContext httpContext,
        bool resolve,
        CancellationToken ct)
    {
        var comment = await commentService.SetResolvedAsync(
            id, CurrentUser.RequireId(httpContext), resolve, ct);

        return Results.Ok(await commentService.BuildDtoAsync(comment, ct));
    }
}

/// <summary>
/// 创建评论请求。
/// </summary>
/// <param name="Path">文件路径，重命名后传当前路径</param>
/// <param name="Side">行所在侧，OLD 或 NEW，忽略大小写</param>
/// <param name="Line">该侧文件中的行号</param>
/// <param name="Body">评论内容，1 到 5000 个字符</param>
/// <param name="ParentId">回复的父评论主键，可空</param>
public sealed record CreateCommentRequest(
    string Path,
    string Side,
    int Line,
    string Body,
    Guid? ParentId);

/// <summary>
/// 修改评论请求。
/// </summary>
/// <param name="Body">新评论内容</param>
public sealed record UpdateCommentRequest(string? Body);

/// <summary>
/// 评论响应。
/// </summary>
/// <param name="Id">评论主键</param>
/// <param name="RevisionId">评论创建时所属评审版本</param>
/// <param name="AuthorId">作者主键</param>
/// <param name="AuthorDisplayName">作者显示名</param>
/// <param name="Path">文件路径</param>
/// <param name="Side">行所在侧</param>
/// <param name="Line">该侧行号</param>
/// <param name="Body">评论内容</param>
/// <param name="ParentId">父评论主键</param>
/// <param name="Status">评论状态</param>
/// <param name="ResolvedBy">解决人主键</param>
/// <param name="ResolvedAt">解决时间</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record CommentDto(
    Guid Id,
    Guid RevisionId,
    Guid AuthorId,
    string AuthorDisplayName,
    string Path,
    string Side,
    int Line,
    string Body,
    Guid? ParentId,
    CommentStatus Status,
    Guid? ResolvedBy,
    DateTime? ResolvedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 评论列表响应。该接口有意不分页：单个评审单的评论量很小。
/// </summary>
/// <param name="Items">评论列表，按创建时间升序</param>
public sealed record CommentListResponse(IReadOnlyList<CommentDto> Items);
