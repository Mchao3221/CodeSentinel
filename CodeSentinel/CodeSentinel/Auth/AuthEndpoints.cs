using System.Security.Claims;
using CodeSentinel.Audit;
using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Auth;

/// <summary>
/// 认证相关端点：登录、登出、当前用户、改密与 CSRF 令牌。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// 登录审计动作标识。
    /// </summary>
    public const string LoginAction = "LOGIN";

    /// <summary>
    /// 映射认证端点。
    /// </summary>
    /// <param name="app">端点路由构建器</param>
    /// <param name="sessionHours">会话有效小时数</param>
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app, int sessionHours)
    {
        app.MapPost("/auth/login", (LoginRequest request, PasswordAuthService authService,
            AuditService auditService, HttpContext httpContext, CancellationToken ct) =>
            LoginAsync(request, authService, auditService, httpContext, sessionHours, ct));

        app.MapPost("/auth/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/auth/me", GetMeAsync).RequireAuthorization();

        app.MapPost("/auth/change-password", (ChangePasswordRequest request, AppDbContext db,
            PasswordAuthService authService, HttpContext httpContext, CancellationToken ct) =>
            ChangePasswordAsync(request, db, authService, httpContext, sessionHours, ct))
            .RequireAuthorization();

        app.MapGet("/auth/csrf", (HttpContext httpContext, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new { token = tokens.RequestToken });
        });
    }

    /// <summary>
    /// 校验密码、写入登录 Cookie 并记录审计。
    /// </summary>
    /// <param name="request">登录请求</param>
    /// <param name="authService">密码服务</param>
    /// <param name="auditService">审计服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="sessionHours">会话有效小时数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当前用户信息</returns>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        PasswordAuthService authService,
        AuditService auditService,
        HttpContext httpContext,
        int sessionHours,
        CancellationToken ct)
    {
        var user = await authService.VerifyAsync(request.Username ?? string.Empty, request.Password ?? string.Empty, ct);

        if (user is null || !user.IsActive)
        {
            throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "INVALID_CREDENTIALS",
                "用户名或密码错误");
        }

        await SignInAsync(httpContext, user, sessionHours);
        await auditService.RecordAsync(
            user.Id,
            LoginAction,
            "USER",
            user.Id,
            new { user.Username },
            ct);

        return Results.Ok(ToMeResponse(user));
    }

    /// <summary>
    /// 读取当前用户的最新信息。
    /// </summary>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当前用户信息</returns>
    private static async Task<IResult> GetMeAsync(
        HttpContext httpContext,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var user = await db.Users.FirstOrDefaultAsync(m => m.Id == userId, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "未登录");

        return Results.Ok(ToMeResponse(user));
    }

    /// <summary>
    /// 校验当前密码后更新密码，并刷新登录 Cookie 中的标记。
    /// </summary>
    /// <param name="request">改密请求</param>
    /// <param name="db">数据库上下文</param>
    /// <param name="authService">密码服务</param>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="sessionHours">会话有效小时数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>无内容</returns>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        AppDbContext db,
        PasswordAuthService authService,
        HttpContext httpContext,
        int sessionHours,
        CancellationToken ct)
    {
        var userId = CurrentUser.RequireId(httpContext);
        var user = await db.Users.FirstOrDefaultAsync(m => m.Id == userId, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "未登录");

        PasswordAuthService.EnsureValidPassword(request.NewPassword);

        if (request.CurrentPassword is null || !authService.VerifyPassword(user, request.CurrentPassword))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "INVALID_PASSWORD",
                "当前密码不正确");
        }

        user.PasswordHash = authService.HashPassword(user, request.NewPassword!);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);

        // 重新签发 Cookie，让强制改密标记立即失效
        await SignInAsync(httpContext, user, sessionHours);

        return Results.NoContent();
    }

    /// <summary>
    /// 按当前用户信息签发 Cookie。
    /// </summary>
    /// <param name="httpContext">HTTP 上下文</param>
    /// <param name="user">用户</param>
    /// <param name="sessionHours">会话有效小时数</param>
    /// <returns></returns>
    private static async Task SignInAsync(HttpContext httpContext, User user, int sessionHours)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(CurrentUser.DisplayNameClaim, user.DisplayName),
            new(CurrentUser.MustChangePasswordClaim, user.MustChangePassword ? "true" : "false")
        };

        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, CurrentUser.AdminRole));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(sessionHours)
        };

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            properties);
    }

    /// <summary>
    /// 转换为当前用户响应。
    /// </summary>
    /// <param name="user">用户</param>
    /// <returns>当前用户响应</returns>
    private static MeResponse ToMeResponse(User user)
    {
        return new MeResponse(user.Id, user.Username, user.DisplayName, user.IsAdmin, user.MustChangePassword);
    }
}

/// <summary>
/// 登录请求。
/// </summary>
/// <param name="Username">登录名</param>
/// <param name="Password">密码</param>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>
/// 修改密码请求。
/// </summary>
/// <param name="CurrentPassword">当前密码</param>
/// <param name="NewPassword">新密码</param>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// 当前用户响应。
/// </summary>
/// <param name="Id">用户主键</param>
/// <param name="Username">登录名</param>
/// <param name="DisplayName">显示名</param>
/// <param name="IsAdmin">是否管理员</param>
/// <param name="MustChangePassword">是否必须修改密码</param>
public sealed record MeResponse(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsAdmin,
    bool MustChangePassword);
