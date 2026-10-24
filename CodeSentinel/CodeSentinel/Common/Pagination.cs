namespace CodeSentinel.Common;

/// <summary>
/// 分页参数归一化：页码收敛到合理上限，页大小夹在 1 与上限之间，避免 (page-1)*pageSize 整数溢出。
/// </summary>
public static class Pagination
{
    /// <summary>
    /// 默认每页条数。
    /// </summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// 每页条数上限。
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// 页码上限，避免超大页码导致整数溢出。
    /// </summary>
    public const int MaxPage = 1_000_000;

    /// <summary>
    /// 归一化页码：非正数回退到 1，超过上限收敛到上限。
    /// </summary>
    /// <param name="page">请求页码，可为空</param>
    /// <returns>归一化后的页码</returns>
    public static int NormalizePage(int? page)
    {
        return page is > 0 ? Math.Min(page.Value, MaxPage) : 1;
    }

    /// <summary>
    /// 归一化每页条数：夹在 1 与上限之间。
    /// </summary>
    /// <param name="pageSize">请求每页条数，可为空</param>
    /// <returns>归一化后的每页条数</returns>
    public static int NormalizePageSize(int? pageSize)
    {
        return Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
    }
}
