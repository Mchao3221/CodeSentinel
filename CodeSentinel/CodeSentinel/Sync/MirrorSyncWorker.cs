using CodeSentinel.Common;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Repos;
using CodeSentinel.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeSentinel.Sync;

/// <summary>
/// 镜像同步后台任务：按仓库同步间隔串行 fetch 到期仓库，失败时记录错误并指数退避。
/// </summary>
public sealed class MirrorSyncWorker : BackgroundService
{
    /// <summary>
    /// 失败退避序列：1、2、4、8、15 分钟，最多 15 分钟。
    /// </summary>
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(15)
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MirrorService _mirrorService;
    private readonly CredentialProtector _credentialProtector;
    private readonly SyncThrottle _syncThrottle;
    private readonly SyncOptions _options;
    private readonly ILogger<MirrorSyncWorker> _logger;

    /// <summary>
    /// 初始化镜像同步后台任务。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂，每轮扫描创建独立 DbContext</param>
    /// <param name="mirrorService">镜像服务</param>
    /// <param name="credentialProtector">仓库凭据解密器</param>
    /// <param name="syncThrottle">同步协调器，保证同一仓库同时只有一个 fetch</param>
    /// <param name="options">同步配置</param>
    /// <param name="logger">日志记录器</param>
    public MirrorSyncWorker(
        IServiceScopeFactory scopeFactory,
        MirrorService mirrorService,
        CredentialProtector credentialProtector,
        SyncThrottle syncThrottle,
        IOptions<SyncOptions> options,
        ILogger<MirrorSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _mirrorService = mirrorService;
        _credentialProtector = credentialProtector;
        _syncThrottle = syncThrottle;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 按固定间隔循环执行同步扫描，单轮异常只记录日志，不终止后台任务。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌</param>
    /// <returns>后台任务</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_options.GetPollSeconds());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "镜像同步扫描失败");
            }
        }
    }

    /// <summary>
    /// 执行一轮同步扫描：串行处理所有到期仓库，单个仓库失败不影响其他仓库。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>扫描任务</returns>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionService = scope.ServiceProvider.GetRequiredService<RevisionService>();
        var mergeDetectionService = scope.ServiceProvider.GetRequiredService<MergeDetectionService>();
        var now = DateTime.UtcNow;

        var repos = await db.Repos
            .Where(m => m.IsActive)
            .OrderBy(m => m.LastFetchedAt)
            .ThenBy(m => m.Name)
            .ToListAsync(ct);

        foreach (var repo in repos)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!IsDue(repo, now))
            {
                continue;
            }

            // 仓库正在被手动同步或其他轮次处理时跳过本轮，不记为失败
            if (!await _syncThrottle.TryEnterAsync(repo.Id, ct))
            {
                _logger.LogDebug("仓库 {RepoId} 正在同步，跳过本轮", repo.Id);
                continue;
            }

            try
            {
                await SyncOneAsync(db, repo, revisionService, mergeDetectionService, ct);
            }
            finally
            {
                _syncThrottle.Exit(repo.Id);
            }
        }
    }

    /// <summary>
    /// 判断仓库本轮是否到期：同步间隔已过且不在退避窗口内。
    /// </summary>
    /// <param name="repo">仓库</param>
    /// <param name="now">当前 UTC 时间</param>
    /// <returns>是否到期</returns>
    private static bool IsDue(Repo repo, DateTime now)
    {
        var intervalElapsed = repo.LastFetchedAt is null
            || repo.LastFetchedAt.Value.AddSeconds(repo.SyncIntervalSeconds) < now;
        var backoffElapsed = repo.NextAttemptAt is null || repo.NextAttemptAt.Value <= now;

        return intervalElapsed && backoffElapsed;
    }

    /// <summary>
    /// 同步单个仓库：成功清空错误与退避，失败累加失败次数并记录脱敏错误。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repo">待同步仓库</param>
    /// <param name="revisionService">评审版本服务，fetch 成功后跟进新提交</param>
    /// <param name="mergeDetectionService">合并检测服务，版本同步成功后跟进合并状态</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>同步任务</returns>
    private async Task SyncOneAsync(
        AppDbContext db,
        Repo repo,
        RevisionService revisionService,
        MergeDetectionService mergeDetectionService,
        CancellationToken ct)
    {
        GitCredentials? credentials = null;

        try
        {
            credentials = RepoCredentials.Read(_credentialProtector, repo);
            await _mirrorService.FetchAsync(repo.Id, repo.GitUrl, credentials, ct);
            ApplySuccess(repo);
            await db.SaveChangesAsync(ct);

            // 版本同步成功后再检测合并与分支删除，避免用旧分支信息误判
            if (await SyncActiveReviewsSafelyAsync(revisionService, repo, ct))
            {
                await SyncMergeStatesSafelyAsync(mergeDetectionService, repo, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            repo.FailureCount += 1;
            repo.LastError = Redact(ex.Message, credentials);
            repo.NextAttemptAt = DateTime.UtcNow.Add(GetBackoff(repo.FailureCount));
            await db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "仓库 {RepoName}（{RepoId}）镜像同步失败，连续第 {FailureCount} 次：{Error}",
                repo.Name,
                repo.Id,
                repo.FailureCount,
                repo.LastError);
        }
    }

    /// <summary>
    /// 立即同步单个仓库：按手动同步语义校验节流，成功后更新仓库状态并跟进活跃评审单。
    /// </summary>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>同步任务</returns>
    public async Task SyncNowAsync(Guid repoId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionService = scope.ServiceProvider.GetRequiredService<RevisionService>();
        var mergeDetectionService = scope.ServiceProvider.GetRequiredService<MergeDetectionService>();
        var repo = await db.Repos.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == repoId, ct)
            ?? throw RepositoryNotFound();

        // 同一仓库同时只允许一个 fetch：worker 正在同步时直接拒绝
        if (!await _syncThrottle.TryEnterAsync(repo.Id, ct))
        {
            throw SyncInProgress();
        }

        try
        {
            if (!_syncThrottle.TryBeginWindow(repo.Id, DateTime.UtcNow))
            {
                throw SyncThrottled();
            }

            var credentials = RepoCredentials.Read(_credentialProtector, repo);
            await _mirrorService.EnsureAsync(repo.Id, repo.GitUrl, credentials, ct);
            await _mirrorService.FetchAsync(repo.Id, repo.GitUrl, credentials, ct);
            await MarkSyncedAsync(db, repo.Id, ct);
            await revisionService.SyncActiveReviewsAsync(repo.Id, ct);
            await SyncMergeStatesSafelyAsync(mergeDetectionService, repo, ct);
        }
        finally
        {
            _syncThrottle.Exit(repo.Id);
        }
    }

    /// <summary>
    /// 跟进仓库活跃评审单的新提交；失败只记录日志，不影响镜像同步结果与退避。
    /// </summary>
    /// <param name="revisionService">评审版本服务</param>
    /// <param name="repo">已同步仓库</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功完成版本同步</returns>
    private async Task<bool> SyncActiveReviewsSafelyAsync(
        RevisionService revisionService,
        Repo repo,
        CancellationToken ct)
    {
        try
        {
            await revisionService.SyncActiveReviewsAsync(repo.Id, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "仓库 {RepoName}（{RepoId}）评审版本同步失败，本轮镜像同步仍视为成功",
                repo.Name,
                repo.Id);
            return false;
        }
    }

    /// <summary>
    /// 跟进仓库的合并状态与分支删除；失败只记录日志，不影响镜像同步结果与退避。
    /// </summary>
    /// <param name="mergeDetectionService">合并检测服务</param>
    /// <param name="repo">已同步仓库</param>
    /// <param name="ct">取消令牌</param>
    /// <returns></returns>
    private async Task SyncMergeStatesSafelyAsync(
        MergeDetectionService mergeDetectionService,
        Repo repo,
        CancellationToken ct)
    {
        try
        {
            await mergeDetectionService.SyncReviewMergeStatesAsync(repo.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "仓库 {RepoName}（{RepoId}）合并状态同步失败，本轮镜像同步仍视为成功",
                repo.Name,
                repo.Id);
        }
    }

    /// <summary>
    /// 同步成功后写回最近同步时间并清空错误、失败次数与退避时间。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    /// <param name="repoId">仓库主键</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新任务</returns>
    private static async Task MarkSyncedAsync(
        AppDbContext db,
        Guid repoId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Repos
            .Where(m => m.Id == repoId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LastFetchedAt, now)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.FailureCount, 0)
                    .SetProperty(m => m.NextAttemptAt, (DateTime?)null)
                    .SetProperty(m => m.UpdatedAt, now),
                ct);
    }

    /// <summary>
    /// 构造“仓库不存在”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException RepositoryNotFound()
    {
        return new ApiException(
            StatusCodes.Status404NotFound,
            "NOT_FOUND",
            "仓库不存在");
    }

    /// <summary>
    /// 构造“仓库正在同步”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException SyncInProgress()
    {
        return new ApiException(
            StatusCodes.Status429TooManyRequests,
            "SYNC_IN_PROGRESS",
            "仓库正在同步，请稍后再试");
    }

    /// <summary>
    /// 构造“同步过于频繁”异常。
    /// </summary>
    /// <returns>业务异常</returns>
    private static ApiException SyncThrottled()
    {
        return new ApiException(
            StatusCodes.Status429TooManyRequests,
            "SYNC_THROTTLED",
            $"同步过于频繁，请 {SyncThrottle.ThrottleSeconds} 秒后再试");
    }

    /// <summary>
    /// 同步成功后清空错误、失败次数与退避时间。
    /// </summary>
    /// <param name="repo">仓库</param>
    private static void ApplySuccess(Repo repo)
    {
        repo.LastFetchedAt = DateTime.UtcNow;
        repo.LastError = null;
        repo.FailureCount = 0;
        repo.NextAttemptAt = null;
    }

    /// <summary>
    /// 按连续失败次数计算退避时长，超过序列长度时固定为 15 分钟。
    /// </summary>
    /// <param name="failureCount">连续失败次数，从 1 开始</param>
    /// <returns>退避时长</returns>
    private static TimeSpan GetBackoff(int failureCount)
    {
        var index = Math.Clamp(failureCount, 1, BackoffSteps.Length) - 1;
        return BackoffSteps[index];
    }

    /// <summary>
    /// 脱敏错误信息中的 URL 内嵌凭据与明文密码，保证日志与库里不出现敏感信息。
    /// </summary>
    /// <param name="message">原始错误信息</param>
    /// <param name="credentials">本次使用的凭据，可为空</param>
    /// <returns>脱敏后的错误信息</returns>
    private static string Redact(string? message, GitCredentials? credentials)
    {
        var redacted = GitUrlRedactor.Redact(message?.Trim()) ?? string.Empty;
        if (credentials is not null && !string.IsNullOrEmpty(credentials.Password))
        {
            redacted = redacted.Replace(credentials.Password, "***", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(redacted) ? "镜像同步失败" : redacted;
    }
}
