namespace CodeSentinel.Sync;

/// <summary>
/// 镜像同步后台任务配置。
/// </summary>
public sealed class SyncOptions
{
    /// <summary>
    /// 默认轮询间隔（秒）。
    /// </summary>
    public const int DefaultPollSeconds = 60;

    /// <summary>
    /// 轮询间隔（秒），小于等于 0 时回退到默认值。
    /// </summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    /// <summary>
    /// 读取有效的轮询间隔秒数。
    /// </summary>
    /// <returns>大于 0 的轮询间隔</returns>
    public int GetPollSeconds()
    {
        return PollSeconds > 0 ? PollSeconds : DefaultPollSeconds;
    }
}
