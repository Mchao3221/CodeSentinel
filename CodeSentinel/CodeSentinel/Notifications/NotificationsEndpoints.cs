using System.Text.Json;
using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSentinel.Notifications;

/// <summary>
/// 通知端点：列表、未读数与标记已读，全部只作用于当前用户。
/// </summary>
public static class NotificationsEndpoints
{
    /// <summary>
    /// 映射通知端点，全部要求登录；非 GET 请求由全局 CSRF 过滤器校验令牌。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/notifications").RequireAuthorization();

        group.MapGet(string.Empty, ListNotificationsAsync);
        group.MapGet("/unread-count", GetUnreadCountAsync);
        group.MapPost("/read", MarkReadAsync);
    }

    /// <summary>
    /// 分页查询当前用户的通知，按创建时间倒序、主键倒序，payload 解析为 JSON 对象返回。
    /// </summary>
    /// <param name="unreadOnly">是否只看未读</param>
    /// <param name="page">页码，从 1 开始</param>
    /// <param name="pageSize">每页条数，默认 20，最大 100</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分页通知列表</returns>
    private static async Task<IResult> ListNotificationsAsync(
        bool? unreadOnly,
        int? page,
        int? pageSize,
        AppDbContext db,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var currentPage = Pagination.NormalizePage(page);
        var currentPageSize = Pagination.NormalizePageSize(pageSize);
        var logger = loggerFactory.CreateLogger("CodeSentinel.Notifications.NotificationsEndpoints");

        var query = db.Notifications.AsNoTracking().Where(m => m.UserId == userId);
        if (unreadOnly == true)
        {
            query = query.Where(m => m.ReadAt == null);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Skip((currentPage - 1) * currentPageSize)
            .Take(currentPageSize)
            .ToListAsync(ct);
        var items = rows
            .Select(m => new NotificationDto(
                m.Id,
                m.Type,
                m.ReviewId,
                ParsePayload(m.PayloadJson, logger),
                m.ReadAt,
                m.CreatedAt))
            .ToList();

        return Results.Ok(new PagedResult<NotificationDto>(
            items, total, currentPage, currentPageSize));
    }

    /// <summary>
    /// 统计当前用户的未读通知数。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>未读数</returns>
    private static async Task<IResult> GetUnreadCountAsync(
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var count = await db.Notifications.AsNoTracking()
            .CountAsync(m => m.UserId == userId && m.ReadAt == null, ct);

        return Results.Ok(new UnreadCountResponse(count));
    }

    /// <summary>
    /// 按 id 列表或全部标记当前用户的通知为已读；已读行不重复计数，两者同时提供时以全部为准。
    /// </summary>
    /// <param name="request">HTTP 请求，手动读取请求体以便统一空请求体的错误形状</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>本次实际更新行数</returns>
    private static async Task<IResult> MarkReadAsync(
        HttpRequest request,
        AppDbContext db,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var body = await ReadMarkReadRequestAsync(request, ct);

        if (body.All is true)
        {
            return Results.Ok(new MarkReadResponse(
                await MarkAllReadAsync(db, userId, ct)));
        }

        if (body.Ids is null)
        {
            throw InvalidBody();
        }

        return Results.Ok(new MarkReadResponse(
            await MarkSelectedReadAsync(db, userId, body.Ids, ct)));
    }

    /// <summary>
    /// 把当前用户的全部未读通知标记为已读。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>实际更新行数</returns>
    private static async Task<int> MarkAllReadAsync(
        AppDbContext db,
        Guid userId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.Notifications
            .Where(m => m.UserId == userId && m.ReadAt == null)
            .ExecuteUpdateAsync(m => m
                .SetProperty(n => n.ReadAt, now)
                .SetProperty(n => n.UpdatedAt, now), ct);
    }

    /// <summary>
    /// 把当前用户指定 id 的未读通知标记为已读，其他用户的行不受影响。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="userId">当前用户主键</param>
    /// <param name="ids">请求中的通知主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>实际更新行数</returns>
    private static async Task<int> MarkSelectedReadAsync(
        AppDbContext db,
        Guid userId,
        IReadOnlyList<Guid> ids,
        CancellationToken ct)
    {
        var targetIds = ids
            .Where(m => m != Guid.Empty)
            .Distinct()
            .ToList();
        if (targetIds.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        return await db.Notifications
            .Where(m => m.UserId == userId
                && m.ReadAt == null
                && targetIds.Contains(m.Id))
            .ExecuteUpdateAsync(m => m
                .SetProperty(n => n.ReadAt, now)
                .SetProperty(n => n.UpdatedAt, now), ct);
    }

    /// <summary>
    /// 读取标记已读请求体：空请求体或既无 ids 也无 all 时返回 400 INVALID_BODY。
    /// </summary>
    /// <param name="request">HTTP 请求</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>请求体</returns>
    private static async Task<MarkReadRequest> ReadMarkReadRequestAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw InvalidBody();
        }

        MarkReadRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<MarkReadRequest>(
                raw, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            // 非法 JSON 属于请求体不合法，而不是服务器错误
            throw InvalidBody();
        }

        if (body is null || (body.All is null && body.Ids is null))
        {
            throw InvalidBody();
        }

        return body;
    }

    /// <summary>
    /// 把 payload JSON 文本解析为可嵌套输出的 JSON 元素，损坏时回退为空对象并记录日志。
    /// </summary>
    /// <param name="payloadJson">payload JSON 文本</param>
    /// <param name="logger">日志记录器</param>
    /// <returns>JSON 元素</returns>
    private static JsonElement ParsePayload(string payloadJson, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // 单条通知的 payload 损坏不应拖垮整个列表，降级为空对象
            logger.LogWarning(ex, "通知 payload 解析失败，回退为空对象");
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>
    /// 构造请求体不合法异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException InvalidBody()
    {
        return new ApiException(
            StatusCodes.Status400BadRequest,
            "INVALID_BODY",
            "请求体必须提供 ids 或 all");
    }
}

/// <summary>
/// 通知项。
/// </summary>
/// <param name="Id">通知主键</param>
/// <param name="Type">通知类型</param>
/// <param name="ReviewId">关联评审单，可空</param>
/// <param name="Payload">payload 对象</param>
/// <param name="ReadAt">阅读时间，空表示未读</param>
/// <param name="CreatedAt">创建时间</param>
public sealed record NotificationDto(
    Guid Id,
    NotificationType Type,
    Guid? ReviewId,
    JsonElement Payload,
    DateTime? ReadAt,
    DateTime CreatedAt);

/// <summary>
/// 未读数响应。
/// </summary>
/// <param name="Count">未读数</param>
public sealed record UnreadCountResponse(int Count);

/// <summary>
/// 标记已读请求，ids 与 all 至少提供一个。
/// </summary>
/// <param name="Ids">待标记的通知主键列表</param>
/// <param name="All">是否标记全部未读</param>
public sealed record MarkReadRequest(IReadOnlyList<Guid>? Ids, bool? All);

/// <summary>
/// 标记已读响应。
/// </summary>
/// <param name="Updated">本次实际更新行数</param>
public sealed record MarkReadResponse(int Updated);
