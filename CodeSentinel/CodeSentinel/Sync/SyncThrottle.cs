namespace CodeSentinel.Sync;

/// <summary>
/// 同步协调器：同一仓库同时只允许一个 fetch，另对同一仓库的手动触发做 10 秒窗口限流。
/// </summary>
public sealed class SyncThrottle
{
    /// <summary>
    /// 手动同步限流窗口（秒）。
    /// </summary>
    public const int ThrottleSeconds = 10;

    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(ThrottleSeconds);

    private readonly object gate = new();
    private readonly Dictionary<Guid, DateTime> lastRuns = [];
    private readonly Dictionary<Guid, SemaphoreSlim> fetchLocks = [];

    /// <summary>
    /// 尝试占用仓库的手动同步窗口。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="now">当前 UTC 时间</param>
    /// <returns>是否允许本次手动同步</returns>
    public bool TryBeginWindow(Guid repoId, DateTime now)
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

    /// <summary>
    /// 非阻塞获取仓库的 fetch 锁，同一仓库同一时间只能有一个成功。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否获取到锁；获取成功后必须调用 Exit 释放</returns>
    public async Task<bool> TryEnterAsync(Guid repoId, CancellationToken ct)
    {
        var semaphore = GetFetchLock(repoId);
        return await semaphore.WaitAsync(TimeSpan.Zero, ct);
    }

    /// <summary>
    /// 释放仓库的 fetch 锁，只能由成功获取锁的调用方执行。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    public void Exit(Guid repoId)
    {
        GetFetchLock(repoId).Release();
    }

    /// <summary>
    /// 读取或创建仓库对应的二值信号量。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <returns>每仓库唯一的信号量</returns>
    private SemaphoreSlim GetFetchLock(Guid repoId)
    {
        lock (gate)
        {
            if (!fetchLocks.TryGetValue(repoId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                fetchLocks[repoId] = semaphore;
            }

            return semaphore;
        }
    }
}
