namespace CodeSentinel.Sync;

/// <summary>
/// 手动同步限流：同一仓库 10 秒内只允许触发一次。
/// </summary>
public sealed class SyncThrottle
{
    /// <summary>
    /// 限流窗口（秒）。
    /// </summary>
    public const int ThrottleSeconds = 10;

    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(ThrottleSeconds);

    private readonly object gate = new();
    private readonly Dictionary<Guid, DateTime> lastRuns = [];

    /// <summary>
    /// 尝试占用仓库的同步窗口。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="now">当前 UTC 时间</param>
    /// <returns>是否允许本次同步</returns>
    public bool TryAcquire(Guid repoId, DateTime now)
    {
        lock (gate)
        {
            if (lastRuns.TryGetValue(repoId, out var last)
                && now - last < ThrottleWindow)
            {
                return false;
            }

            lastRuns[repoId] = now;
            return true;
        }
    }
}
