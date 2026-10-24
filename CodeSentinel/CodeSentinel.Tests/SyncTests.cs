using System.Net.Http.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using CodeSentinel.Git;
using CodeSentinel.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 镜像同步后台任务测试。
/// </summary>
public class SyncTests
{
    /// <summary>
    /// 到期的仓库应被 fetch，并写入 last_fetched_at、清空错误与退避字段。
    /// </summary>
    [Fact]
    public async Task Worker_fetches_due_repo_and_updates_last_fetched_at()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("due"));

        var worker = factory.Services.GetRequiredService<MirrorSyncWorker>();
        await worker.RunOnceAsync(CancellationToken.None);

        var stored = await ReadRepoAsync(factory, repo.Id);
        Assert.NotNull(stored.LastFetchedAt);
        Assert.Null(stored.LastError);
        Assert.Null(stored.NextAttemptAt);
        Assert.Equal(0, stored.FailureCount);

        // 镜像里应已经存在 main 分支
        var mirror = factory.Services.GetRequiredService<MirrorService>();
        var head = await mirror.GetHeadAsync(repo.Id, "main", CancellationToken.None);
        Assert.Equal(source.MainSha, head);
    }

    /// <summary>
    /// 同步失败应记录脱敏后的错误并按 1/2/4/8/15 分钟递增退避，且不影响其他仓库。
    /// </summary>
    [Fact]
    public async Task Worker_records_last_error_and_backs_off()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var healthy = factory.SeedRepo(source.Url, UniqueName("healthy"));
        var broken = factory.SeedRepo("http://127.0.0.1:1/repo.git", UniqueName("broken"));

        var worker = factory.Services.GetRequiredService<MirrorSyncWorker>();
        var expectedMinutes = new[] { 1, 2, 4, 8, 15 };

        for (var index = 0; index < expectedMinutes.Length; index++)
        {
            await MakeDueAsync(factory, broken.Id);

            var before = DateTime.UtcNow;
            await worker.RunOnceAsync(CancellationToken.None);
            var stored = await ReadRepoAsync(factory, broken.Id);

            Assert.Equal(index + 1, stored.FailureCount);
            Assert.NotNull(stored.LastError);
            Assert.Contains("无法连接仓库", stored.LastError, StringComparison.Ordinal);
            Assert.NotNull(stored.NextAttemptAt);

            var actualMinutes = (stored.NextAttemptAt!.Value - before).TotalMinutes;
            Assert.InRange(actualMinutes, expectedMinutes[index] - 0.5, expectedMinutes[index] + 0.5);
        }

        // 坏仓库连续失败时不能中断扫描，健康仓库只成功一次
        var healthyStored = await ReadRepoAsync(factory, healthy.Id);
        Assert.NotNull(healthyStored.LastFetchedAt);
        Assert.Null(healthyStored.LastError);
    }

    /// <summary>
    /// 手动触发同步成功后写回同步时间，10 秒内重复触发返回 429。
    /// </summary>
    [Fact]
    public async Task Manual_sync_updates_repo_and_throttles_repeat()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("manual"));
        var admin = await factory.LoginAsAdminAsync();

        var first = await admin.PostJsonWithCsrfAsync($"/api/repos/{repo.Id}/sync", new { });
        var firstBody = await first.Content.ReadFromJsonAsync<SyncedBody>();

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(firstBody);
        Assert.True(firstBody.Synced);
        Assert.NotNull((await ReadRepoAsync(factory, repo.Id)).LastFetchedAt);

        var second = await admin.PostJsonWithCsrfAsync($"/api/repos/{repo.Id}/sync", new { });
        var error = await second.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("SYNC_THROTTLED", error.Code);
    }

    /// <summary>
    /// 非成员触发同步应返回 404，不暴露仓库存在性。
    /// </summary>
    [Fact]
    public async Task Manual_sync_requires_repo_access()
    {
        using var factory = new TestAppFactory();
        using var source = await TestGitRepo.CreateAsync();
        var repo = factory.SeedRepo(source.Url, UniqueName("private-sync"));
        var outsider = factory.SeedUser(UniqueName("outsider"), "Passw0rd!");

        var client = await factory.LoginAsync(outsider.Username, "Passw0rd!");
        var response = await client.PostJsonWithCsrfAsync($"/api/repos/{repo.Id}/sync", new { });
        var error = await response.Content.ReadFromJsonAsync<ErrorBody>();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
    }

    /// <summary>
    /// 把仓库的下次重试时间改为过去，便于连续触发同一失败仓库。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <returns></returns>
    private static async Task MakeDueAsync(TestAppFactory factory, Guid repoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = await db.Repos.SingleAsync(m => m.Id == repoId);
        repo.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 读取仓库最新状态。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="repoId">仓库主键</param>
    /// <returns>只读仓库</returns>
    private static async Task<Repo> ReadRepoAsync(TestAppFactory factory, Guid repoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Repos.AsNoTracking().SingleAsync(m => m.Id == repoId);
    }

    /// <summary>
    /// 生成不重复的仓库名称。
    /// </summary>
    /// <param name="prefix">名称前缀</param>
    /// <returns>唯一名称</returns>
    private static string UniqueName(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }
}

/// <summary>
/// 手动同步响应 DTO。
/// </summary>
/// <param name="Synced">是否完成同步</param>
public sealed record SyncedBody(bool Synced);
