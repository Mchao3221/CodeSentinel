using CodeSentinel.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSentinel.Tests;

/// <summary>
/// 集成测试宿主工厂：每个实例使用独立的临时 SQLite 文件，销毁时删除。
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"codesentinel-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 初始化工厂，校验连接串生效并创建测试数据库结构。
    /// </summary>
    public TestAppFactory()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configuredConnectionString = db.Database.GetConnectionString();

        if (!string.Equals(configuredConnectionString, ConnectionString, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"测试数据库连接串未生效，实际为：{configuredConnectionString}");
        }

        db.Database.EnsureCreated();
    }

    /// <summary>
    /// 测试数据库文件路径。
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// 测试数据库连接串。
    /// </summary>
    private string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    /// <summary>
    /// 覆盖宿主配置：使用 Testing 环境，并在 appsettings 之后追加内存配置，确保隔离到临时库。
    /// </summary>
    /// <param name="builder">宿主构建器</param>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString
            });
        });
    }

    /// <summary>
    /// 释放宿主并删除临时数据库文件。
    /// </summary>
    /// <param name="disposing">是否正在释放托管资源</param>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
