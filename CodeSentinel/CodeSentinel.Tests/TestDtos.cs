namespace CodeSentinel.Tests;

/// <summary>
/// 统一错误响应的测试 DTO。
/// </summary>
/// <param name="Code">错误码</param>
/// <param name="Message">错误信息</param>
/// <param name="TraceId">请求追踪标识</param>
public sealed record ErrorBody(string Code, string Message, string TraceId);
