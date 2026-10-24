namespace CodeSentinel.Common;

/// <summary>
/// 全局异常中间件：把业务异常与未处理异常统一转换成错误响应，避免泄露堆栈。
/// </summary>
/// <param name="next">下一个中间件</param>
/// <param name="logger">日志记录器</param>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    /// <summary>
    /// 执行请求并捕获异常。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <returns></returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await WriteErrorAsync(context, ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理的异常，traceId={TraceId}", context.TraceIdentifier);
            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "服务器内部错误");
        }
    }

    /// <summary>
    /// 按统一形状写出错误响应。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="status">HTTP 状态码</param>
    /// <param name="code">业务错误码</param>
    /// <param name="message">错误信息</param>
    /// <returns></returns>
    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(code, message, context.TraceIdentifier));
    }

    /// <summary>
    /// 错误响应 DTO。
    /// </summary>
    /// <param name="Code">错误码</param>
    /// <param name="Message">错误信息</param>
    /// <param name="TraceId">请求追踪标识</param>
    private sealed record ErrorResponse(string Code, string Message, string TraceId);
}
