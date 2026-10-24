using CodeSentinel.Data;
using Microsoft.Data.Sqlite;

namespace CodeSentinel.Tests;

/// <summary>
/// SQLite 连接配置测试：WAL 日志模式与 busy_timeout。
/// </summary>
public class SqliteConfigurationTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    /// <summary>
    /// 测试库已启用 WAL，且共享配置为每条连接设置正数 busy_timeout。
    /// </summary>
    [Fact]
    public async Task Database_uses_wal_and_positive_busy_timeout()
    {
        await using var connection = new SqliteConnection($"Data Source={factory.DatabasePath}");
        await connection.OpenAsync();

        // WAL 由启动逻辑持久化到库文件，任何连接都能读到
        var mode = await ReadPragmaAsync(connection, "PRAGMA journal_mode;");
        Assert.Equal("wal", mode);

        // busy_timeout 是连接级设置，由共享配置应用到每条连接（EF 连接经拦截器使用同一来源）
        SqliteConfiguration.ApplyPragmas(connection);
        var busyTimeout = Convert.ToInt64(await ReadPragmaAsync(connection, "PRAGMA busy_timeout;"));
        Assert.True(busyTimeout > 0);
    }

    /// <summary>
    /// 读取单个 PRAGMA 的标量结果。
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接</param>
    /// <param name="sql">PRAGMA 语句</param>
    /// <returns>标量结果的字符串表示</returns>
    private static async Task<string?> ReadPragmaAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }
}
