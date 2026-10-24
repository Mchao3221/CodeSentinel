using System.Security.Claims;
using CodeSentinel.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace CodeSentinel.Auth;

/// <summary>
/// 会话主体验证器：每次请求都把 Cookie 中的用户与数据库核对，停用即失效，角色与改密标记即时刷新。
/// </summary>
public static class SessionValidator
{
    /// <summary>
    /// 校验 Cookie 主体与数据库一致：用户不存在或已停用时拒绝主体，角色或改密标记变化时重建主体。
    /// </summary>
    /// <param name="context">Cookie 校验上下文</param>
    /// <returns></returns>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            context.RejectPrincipal();
            return;
        }

        var nameIdentifier = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(nameIdentifier, out var userId))
        {
            context.RejectPrincipal();
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(m => m.Id == userId);

        // 用户被删除或停用后立即失效，无需等待 Cookie 过期
        if (user is null || !user.IsActive)
        {
            context.RejectPrincipal();
            return;
        }

        var isAdmin = principal.IsInRole(CurrentUser.AdminRole);
        var mustChange = string.Equals(
            principal.FindFirstValue(CurrentUser.MustChangePasswordClaim),
            "true",
            StringComparison.OrdinalIgnoreCase);

        // 角色或改密标记与数据库不一致时重建主体，让降级与重置密码立即生效
        if (user.IsAdmin != isAdmin || user.MustChangePassword != mustChange)
        {
            context.ReplacePrincipal(AuthEndpoints.BuildPrincipal(user));
            context.ShouldRenew = true;
        }
    }
}
