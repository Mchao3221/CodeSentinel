using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSentinel.Data;

/// <summary>
/// SQLite 连接拦截器：每条连接打开后应用 WAL 与 busy_timeout。
/// </summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// 同步打开连接后应用连接级 PRAGMA。
    /// </summary>
    /// <param name="connection">已打开的数据库连接</param>
    /// <param name="eventData">连接事件数据</param>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    /// <summary>
    /// 异步打开连接后应用连接级 PRAGMA。
    /// </summary>
    /// <param name="connection">已打开的数据库连接</param>
    /// <param name="eventData">连接事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>完成的任务</returns>
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 仅对 SQLite 连接应用共享的 PRAGMA 配置。
    /// </summary>
    /// <param name="connection">已打开的数据库连接</param>
    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            SqliteConfiguration.ApplyPragmas(sqlite);
        }
    }
}
