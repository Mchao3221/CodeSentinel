using Microsoft.Data.Sqlite;

namespace CodeSentinel.Data;

/// <summary>
/// SQLite 连接级配置：WAL 日志模式与 busy_timeout。
/// </summary>
public static class SqliteConfiguration
{
    /// <summary>
    /// 数据库忙等待超时（毫秒），并发写入时避免直接报 database is locked。
    /// </summary>
    public const int BusyTimeoutMilliseconds = 30000;

    /// <summary>
    /// 打开连接后应用 WAL 与 busy_timeout。
    /// WAL 是库文件级持久设置，重复执行幂等；busy_timeout 是连接级设置，每条连接都需执行。
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接</param>
    public static void ApplyPragmas(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
    }

    /// <summary>
    /// 在连接上执行一条 PRAGMA，忽略返回行。
    /// </summary>
    /// <param name="connection">已打开的 SQLite 连接</param>
    /// <param name="sql">要执行的 PRAGMA 语句</param>
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
