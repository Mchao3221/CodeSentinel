namespace CodeSentinel.Tests;

/// <summary>
/// 统一错误响应的测试 DTO。
/// </summary>
/// <param name="Code">错误码</param>
/// <param name="Message">错误信息</param>
/// <param name="TraceId">请求追踪标识</param>
public sealed record ErrorBody(string Code, string Message, string TraceId);

/// <summary>
/// CSRF 令牌响应的测试 DTO。
/// </summary>
/// <param name="Token">请求头使用的令牌</param>
public sealed record CsrfBody(string Token);

/// <summary>
/// 当前用户响应的测试 DTO。
/// </summary>
/// <param name="Id">用户主键</param>
/// <param name="Username">登录名</param>
/// <param name="DisplayName">显示名</param>
/// <param name="IsAdmin">是否管理员</param>
/// <param name="MustChangePassword">是否必须修改密码</param>
public sealed record MeBody(Guid Id, string Username, string DisplayName, bool IsAdmin, bool MustChangePassword);

/// <summary>
/// 用户响应的测试 DTO。
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
public sealed record UserBody(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    bool IsActive,
    bool MustChangePassword,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 用户列表响应的测试 DTO。
/// </summary>
/// <param name="Items">当前页数据</param>
/// <param name="Total">总条数</param>
/// <param name="Page">当前页码</param>
/// <param name="PageSize">每页条数</param>
public sealed record UserListBody(IReadOnlyList<UserBody> Items, int Total, int Page, int PageSize);

/// <summary>
/// 重置密码响应的测试 DTO。
/// </summary>
/// <param name="Password">一次性密码</param>
public sealed record ResetPasswordBody(string Password);
