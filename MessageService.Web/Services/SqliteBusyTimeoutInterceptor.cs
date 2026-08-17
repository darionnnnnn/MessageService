using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MessageService.Services;

/// <summary>SQLite 連線開啟後設定 busy_timeout（毫秒）。
/// 當 SQLite 寫鎖被其他行程或執行緒佔用時，最多等待指定時間才放棄，避免直接拋出 SQLITE_BUSY 錯誤。</summary>
public class SqliteBusyTimeoutInterceptor(int busyTimeoutMs = SqliteBusyTimeoutInterceptor.DefaultBusyTimeoutMs) : DbConnectionInterceptor
{
    public const int DefaultBusyTimeoutMs = 30000;

    public static string BuildPragmaCommandText(int timeoutMs) => $"PRAGMA busy_timeout={timeoutMs};";

    public static void SetBusyTimeout(DbConnection connection, int timeoutMs)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = BuildPragmaCommandText(timeoutMs);
        command.ExecuteNonQuery();
    }

    public static async Task SetBusyTimeoutAsync(DbConnection connection, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = BuildPragmaCommandText(timeoutMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetBusyTimeout(connection, busyTimeoutMs);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetBusyTimeoutAsync(connection, busyTimeoutMs, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
