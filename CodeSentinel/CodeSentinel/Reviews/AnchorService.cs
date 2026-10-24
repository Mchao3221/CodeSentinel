namespace CodeSentinel.Reviews;

/// <summary>
/// 重新锚定结果：保留、移动与失效的评论数量。
/// </summary>
/// <param name="Kept">保持不变的数量</param>
/// <param name="Moved">成功移动的数量</param>
/// <param name="Outdated">失效的数量</param>
public sealed record ReanchorReport(int Kept, int Moved, int Outdated);

/// <summary>
/// 评论重新锚定服务。Task 11 之前为空实现，保证新版本流程可以先行落地。
/// </summary>
public sealed class AnchorService
{
    /// <summary>
    /// 把旧版本上的评论重新锚定到新版本，当前为空实现。
    /// </summary>
    /// <param name="reviewId">评审单主键</param>
    /// <param name="fromRevisionId">旧版本主键</param>
    /// <param name="toRevisionId">新版本主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>重新锚定结果</returns>
    public Task<ReanchorReport> ReanchorAsync(
        Guid reviewId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken ct)
    {
        return Task.FromResult(new ReanchorReport(0, 0, 0));
    }
}
