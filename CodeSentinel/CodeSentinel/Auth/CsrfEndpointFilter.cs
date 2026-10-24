using CodeSentinel.Common;
using Microsoft.AspNetCore.Antiforgery;

namespace CodeSentinel.Auth;

/// <summary>
/// 全局 CSRF 端点过滤器：所有非 GET 请求（登录与初始化除外）必须携带有效令牌。
/// </summary>
/// <param name="antiforgery">防伪令牌服务</param>
public sealed class CsrfEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    private static readonly PathString loginPath = new("/api/auth/login");
    private static readonly PathString setupAdminPath = new("/api/setup/admin");

    /// <summary>
    /// 先校验登录状态，再校验 CSRF 令牌。
    /// </summary>
    /// <param name="context">端点过滤器上下文</param>
    /// <param name="next">后续处理委托</param>
    /// <returns>响应</returns>
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (HttpMethods.IsGet(httpContext.Request.Method) || ShouldSkip(httpContext.Request.Path))
        {
            return await next(context);
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "未登录");
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "CSRF_INVALID",
                "CSRF 令牌缺失或无效");
        }

        return await next(context);
    }

    /// <summary>
    /// 判断请求是否免于 CSRF 校验。
    /// </summary>
    /// <param name="path">请求路径</param>
    /// <returns>是否跳过</returns>
    private static bool ShouldSkip(PathString path)
    {
        return path.Equals(loginPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(setupAdminPath, StringComparison.OrdinalIgnoreCase);
    }
}
