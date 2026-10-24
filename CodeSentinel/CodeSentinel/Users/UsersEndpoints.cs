using System.Security.Cryptography;
using System.Text.Json;
using CodeSentinel.Auth;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Users;

/// <summary>
/// 管理员用户管理端点。
/// </summary>
public static class UsersEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int ResetPasswordLength = 16;
    private const int MaxUsernameLength = 64;
    private const int MaxDisplayNameLength = 100;
    private const int MaxEmailLength = 256;

    // 去掉易混淆的 0/O/1/I/l/o，便于管理员转述一次性密码
    private const string ResetPasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private static readonly JsonSerializerOptions notificationJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 映射用户管理端点，全部要求管理员身份。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    public static void MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users").RequireAuthorization(CurrentUser.AdminRole);

        group.MapGet(string.Empty, ListUsersAsync);

        group.MapPost(string.Empty, CreateUserAsync);

        group.MapPatch("/{id:guid}", PatchUserAsync);

        group.MapPost("/{id:guid}/reset-password", ResetPasswordAsync);
    }

    /// <summary>
    /// 分页查询用户，按更新时间倒序。
    /// </summary>
    /// <param name="page">页码，从 1 开始</param>
    /// <param name="pageSize">每页条数，默认 20，最大 100</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分页用户列表</returns>
    private static async Task<IResult> ListUsersAsync(
        int? page,
        int? pageSize,
        AppDbContext db,
        CancellationToken ct)
    {
        var currentPage = page is > 0 ? page.Value : 1;
        var currentPageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Users.AsNoTracking().OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Username);

        var total = await query.CountAsync(ct);
        var users = await query
            .Skip((currentPage - 1) * currentPageSize)
            .Take(currentPageSize)
            .ToListAsync(ct);

        var items = users.Select(ToUserResponse).ToList();
        return Results.Ok(new PagedResult<UserResponse>(items, total, currentPage, currentPageSize));
    }

    /// <summary>
    /// 创建用户，初始密码需在首次登录后修改。
    /// </summary>
    /// <param name="request">创建用户请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="authService">密码服务</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的用户</returns>
    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        AppDbContext db,
        PasswordAuthService authService,
        CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;

        ValidateUserFields(username, displayName, request.Email);
        PasswordAuthService.EnsureValidPassword(request.Password);

        if (await UsernameExistsAsync(db, username, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "USERNAME_EXISTS", "用户名已存在");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            Email = NormalizeEmail(request.Email),
            IsAdmin = request.IsAdmin,
            IsActive = true,
            MustChangePassword = true
        };
        user.PasswordHash = authService.HashPassword(user, request.Password!);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/users/{user.Id}", ToUserResponse(user));
    }

    /// <summary>
    /// 局部更新用户：停用、改名、调整管理员标记。
    /// </summary>
    /// <param name="id">用户主键</param>
    /// <param name="request">更新请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新后的用户</returns>
    private static async Task<IResult> PatchUserAsync(
        Guid id,
        PatchUserRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "用户不存在");

        ValidatePatchRequest(request);

        if (request.DisplayName is not null)
        {
            user.DisplayName = request.DisplayName.Trim();
        }

        var deactivating = request.IsActive == false && user.IsActive;
        var demoting = request.IsAdmin == false && user.IsAdmin;

        if ((deactivating || demoting) && user.IsAdmin && user.IsActive)
        {
            var activeAdminCount = await db.Users.CountAsync(m => m.IsAdmin && m.IsActive, ct);
            if (activeAdminCount <= 1)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "LAST_ADMIN",
                    "不能停用或降级最后一个启用中的管理员");
            }
        }

        if (request.IsAdmin is not null)
        {
            user.IsAdmin = request.IsAdmin.Value;
        }

        if (request.IsActive is not null)
        {
            user.IsActive = request.IsActive.Value;
        }

        if (deactivating)
        {
            await RemoveReviewerFromActiveReviewsAsync(db, user, ct);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToUserResponse(user));
    }

    /// <summary>
    /// 重置密码，返回一次性新密码并强制下次登录后修改。
    /// </summary>
    /// <param name="id">用户主键</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="authService">密码服务</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>一次性新密码</returns>
    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        AppDbContext db,
        PasswordAuthService authService,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "NOT_FOUND", "用户不存在");

        var newPassword = RandomNumberGenerator.GetString(ResetPasswordAlphabet, ResetPasswordLength);
        user.PasswordHash = authService.HashPassword(user, newPassword);
        user.MustChangePassword = true;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { password = newPassword });
    }

    /// <summary>
    /// 停用用户时，把其未结束评审中的审核人状态置为 REMOVED，并通知提交人补人。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="user">被停用的用户</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private static async Task RemoveReviewerFromActiveReviewsAsync(
        AppDbContext db,
        User user,
        CancellationToken ct)
    {
        var assignments = await (
            from reviewer in db.ReviewReviewers
            join review in db.Reviews on reviewer.ReviewId equals review.Id
            where reviewer.UserId == user.Id
                && reviewer.State != ReviewerState.Removed
                && review.Status != ReviewStatus.Merged
                && review.Status != ReviewStatus.Closed
            select new
            {
                Reviewer = reviewer,
                ReviewId = review.Id,
                review.SubmitterId,
                review.Title
            }).ToListAsync(ct);

        if (assignments.Count == 0)
        {
            return;
        }

        foreach (var assignment in assignments)
        {
            assignment.Reviewer.State = ReviewerState.Removed;
        }

        var notifications = assignments
            .GroupBy(m => new { m.ReviewId, m.SubmitterId, m.Title })
            .Select(group => new Notification
            {
                Id = Guid.NewGuid(),
                UserId = group.Key.SubmitterId,
                Type = NotificationType.ReviewerRemoved,
                ReviewId = group.Key.ReviewId,
                PayloadJson = JsonSerializer.Serialize(
                    new
                    {
                        reviewId = group.Key.ReviewId,
                        title = group.Key.Title,
                        removedUserId = user.Id,
                        removedDisplayName = user.DisplayName,
                        reason = "USER_DEACTIVATED"
                    },
                    notificationJsonOptions)
            });

        db.Notifications.AddRange(notifications);
    }

    /// <summary>
    /// 校验新建用户字段。
    /// </summary>
    /// <param name="username">登录名</param>
    /// <param name="displayName">显示名</param>
    /// <param name="email">邮箱</param>
    private static void ValidateUserFields(string username, string displayName, string? email)
    {
        if (username.Length is 0 or > MaxUsernameLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_USERNAME",
                $"用户名不能为空且不超过 {MaxUsernameLength} 个字符");
        }

        if (displayName.Length is 0 or > MaxDisplayNameLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_DISPLAY_NAME",
                $"显示名不能为空且不超过 {MaxDisplayNameLength} 个字符");
        }

        if (email is not null && email.Trim().Length > MaxEmailLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_EMAIL",
                $"邮箱不超过 {MaxEmailLength} 个字符");
        }
    }

    /// <summary>
    /// 校验局部更新请求。
    /// </summary>
    /// <param name="request">更新请求</param>
    private static void ValidatePatchRequest(PatchUserRequest request)
    {
        if (request.DisplayName is not null && request.DisplayName.Trim().Length is 0 or > MaxDisplayNameLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_DISPLAY_NAME",
                $"显示名不能为空且不超过 {MaxDisplayNameLength} 个字符");
        }
    }

    /// <summary>
    /// 判断用户名是否已存在（大小写不敏感）。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="username">登录名</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否已存在</returns>
    private static Task<bool> UsernameExistsAsync(AppDbContext db, string username, CancellationToken ct)
    {
        var normalized = username.ToLowerInvariant();
        return db.Users.AnyAsync(m => m.Username.ToLower() == normalized, ct);
    }

    /// <summary>
    /// 归一化邮箱：去空白，空串转空值。
    /// </summary>
    /// <param name="email">邮箱</param>
    /// <returns>归一化后的邮箱</returns>
    private static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 转换为用户响应。
    /// </summary>
    /// <param name="user">用户</param>
    /// <returns>用户响应</returns>
    private static UserResponse ToUserResponse(User user)
    {
        return new UserResponse(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Email,
            user.IsAdmin,
            user.IsActive,
            user.MustChangePassword,
            user.CreatedAt,
            user.UpdatedAt);
    }
}

/// <summary>
/// 创建用户请求。
/// </summary>
/// <param name="Username">登录名</param>
/// <param name="Password">初始密码</param>
/// <param name="DisplayName">显示名</param>
/// <param name="Email">邮箱</param>
/// <param name="IsAdmin">是否管理员</param>
public sealed record CreateUserRequest(
    string? Username,
    string? Password,
    string? DisplayName,
    string? Email,
    bool IsAdmin);

/// <summary>
/// 局部更新用户请求，未提供的字段保持不变。
/// </summary>
/// <param name="IsActive">是否启用</param>
/// <param name="DisplayName">显示名</param>
/// <param name="IsAdmin">是否管理员</param>
public sealed record PatchUserRequest(bool? IsActive, string? DisplayName, bool? IsAdmin);

/// <summary>
/// 用户响应。
/// </summary>
/// <param name="Id">用户主键</param>
/// <param name="Username">登录名</param>
/// <param name="DisplayName">显示名</param>
/// <param name="Email">邮箱</param>
/// <param name="IsAdmin">是否管理员</param>
/// <param name="IsActive">是否启用</param>
/// <param name="MustChangePassword">是否必须修改密码</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record UserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    bool IsActive,
    bool MustChangePassword,
    DateTime CreatedAt,
    DateTime UpdatedAt);
