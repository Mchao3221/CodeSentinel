using CodeSentinel.Common;

namespace CodeSentinel.Auth;

/// <summary>
/// 强制改密中间件：带 must_change_password 标记的用户只能访问改密相关接口。
/// </summary>
/// <param name="next">下一个中间件</param>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly PathString changePasswordPath = new("/api/auth/change-password");
    private static readonly PathString logoutPath = new("/api/auth/logout");
    private static readonly PathString mePath = new("/api/auth/me");
    private static readonly PathString csrfPath = new("/api/auth/csrf");

    /// <summary>
    /// 校验当前用户是否仍处于待改密状态。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns></returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!CurrentUser.MustChangePassword(context) || IsAllowed(context.Request))
        {
            await next(context);
            return;
        }

        throw new ApiException(
            StatusCodes.Status403Forbidden,
            "PASSWORD_CHANGE_REQUIRED",
            "请先修改初始密码");
    }

    /// <summary>
    /// 判断请求是否属于改密期间允许访问的接口。
    /// </summary>
    /// <param name="request">HTTP 请求</param>
    /// <returns>是否允许</returns>
    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path;

        if (HttpMethods.IsGet(request.Method))
        {
            return path.Equals(mePath, StringComparison.OrdinalIgnoreCase)
                || path.Equals(csrfPath, StringComparison.OrdinalIgnoreCase);
        }

        if (HttpMethods.IsPost(request.Method))
        {
            return path.Equals(changePasswordPath, StringComparison.OrdinalIgnoreCase)
                || path.Equals(logoutPath, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
