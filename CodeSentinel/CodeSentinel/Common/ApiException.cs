namespace CodeSentinel.Common;

/// <summary>
/// 业务异常，由全局中间件转换为统一错误响应。
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>
    /// HTTP 状态码。
    /// </summary>
    public int Status { get; }

    /// <summary>
    /// 业务错误码。
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// 初始化业务异常。
    /// </summary>
    /// <param name="status">HTTP 状态码</param>
    /// <param name="code">业务错误码</param>
    /// <param name="message">错误信息</param>
    public ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}
