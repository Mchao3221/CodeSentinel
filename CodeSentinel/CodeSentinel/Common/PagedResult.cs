namespace CodeSentinel.Common;

/// <summary>
/// 统一分页响应。
/// </summary>
/// <typeparam name="T">列表项类型</typeparam>
/// <param name="Items">当前页数据</param>
/// <param name="Total">总条数</param>
/// <param name="Page">当前页码</param>
/// <param name="PageSize">每页条数</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
