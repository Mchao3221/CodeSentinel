using System.Text.Json;
using CodeSentinel.Data;
using CodeSentinel.Data.Entities;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeSentinel.Tests;

/// <summary>
/// 持久化基座测试：UTC 时间戳维护与 JSON 序列化。
/// </summary>
public class PersistenceTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    /// <summary>
    /// 新增实体时自动写入 UTC 类型的创建/更新时间。
    /// </summary>
    [Fact]
    public async Task Saving_sets_utc_timestamps()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = CreateUser();

        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.NotEqual(default, user.CreatedAt);
        Assert.NotEqual(default, user.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, user.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, user.UpdatedAt.Kind);
    }

    /// <summary>
    /// 修改实体时只有更新时间前进，创建时间保持不变。
    /// </summary>
    [Fact]
    public async Task Updating_advances_updated_at_and_keeps_created_at()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var createdAt = user.CreatedAt;
        var updatedAt = user.UpdatedAt;

        await Task.Delay(20);
        user.DisplayName = "修改后的名称";
        await db.SaveChangesAsync();

        Assert.Equal(createdAt, user.CreatedAt);
        Assert.True(user.UpdatedAt > updatedAt);
    }

    /// <summary>
    /// 从数据库读回的时间应带 UTC Kind（含可空时间列）。
    /// </summary>
    [Fact]
    public async Task Reading_back_returns_utc_kind()
    {
        Guid userId;
        Guid repoId;

        using (var writeScope = factory.Services.CreateScope())
        {
            var writeDb = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = CreateUser();
            var repo = new Repo
            {
                Id = Guid.NewGuid(),
                Name = $"repo-{Guid.NewGuid():N}",
                GitUrl = "https://example.com/repo.git",
                DefaultBaseBranch = "main",
                LastFetchedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)
            };

            writeDb.Users.Add(user);
            writeDb.Repos.Add(repo);
            await writeDb.SaveChangesAsync();

            userId = user.Id;
            repoId = repo.Id;
        }

        using var readScope = factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loadedUser = await readDb.Users.SingleAsync(m => m.Id == userId);
        var loadedRepo = await readDb.Repos.SingleAsync(m => m.Id == repoId);

        Assert.Equal(DateTimeKind.Utc, loadedUser.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedUser.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedRepo.LastFetchedAt!.Value.Kind);
    }

    /// <summary>
    /// 使用应用配置的 JSON 选项序列化时，时间应输出为带 Z 的 ISO-8601。
    /// </summary>
    [Fact]
    public async Task Json_serialization_outputs_utc_iso8601()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var loadedUser = await db.Users.SingleAsync(m => m.Id == user.Id);

        var jsonOptions = factory.Services
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .SerializerOptions;
        var json = JsonSerializer.Serialize(
            new { loadedUser.CreatedAt, loadedUser.UpdatedAt },
            jsonOptions);

        using var document = JsonDocument.Parse(json);
        Assert.EndsWith("Z", document.RootElement.GetProperty("createdAt").GetString());
        Assert.EndsWith("Z", document.RootElement.GetProperty("updatedAt").GetString());
    }

    /// <summary>
    /// 创建测试用户。
    /// </summary>
    /// <returns>测试用户</returns>
    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = $"user-{Guid.NewGuid():N}",
        DisplayName = "测试用户",
        PasswordHash = "hash"
    };
}
