using System.Security.Claims;
using CodeSentinel.Common;

namespace CodeSentinel.Auth;

/// <summary>
/// 从 HttpContext 读取当前登录用户信息。
/// </summary>
public static class CurrentUser
{
    /// <summary>
    /// 管理员角色名。
    /// </summary>
    public const string AdminRole = "Admin";

    /// <summary>
    /// 显示名声明名。
    /// </summary>
    public const string DisplayNameClaim = "display_name";

    /// <summary>
    /// 是否必须修改密码的声明名。
    /// </summary>
    public const string MustChangePasswordClaim = "must_change_password";

    /// <summary>
    /// 取当前用户主键，未登录时返回空。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns>用户主键</returns>
    public static Guid? Id(HttpContext context)
    {
        var value = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// 判断当前用户是否为管理员。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns>是否管理员</returns>
    public static bool IsAdmin(HttpContext context)
    {
        return context.User.IsInRole(AdminRole);
    }

    /// <summary>
    /// 取当前用户主键，未登录时抛出 401。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns>用户主键</returns>
    public static Guid RequireId(HttpContext context)
    {
        return Id(context)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "未登录");
    }

    /// <summary>
    /// 判断当前用户是否必须先修改密码。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns>是否必须修改密码</returns>
    public static bool MustChangePassword(HttpContext context)
    {
        return string.Equals(
            context.User.FindFirstValue(MustChangePasswordClaim),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }
}
