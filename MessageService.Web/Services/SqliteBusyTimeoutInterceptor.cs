using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MessageService.Services;

/// <summary>SQLite 連線開啟後設定 busy_timeout（毫秒）。
/// 當 SQLite 寫鎖被其他行程或執行緒佔用時，最多等待指定時間才放棄，避免直接拋出 SQLITE_BUSY 錯誤。</summary>
public class SqliteBusyTimeoutInterceptor(int busyTimeoutMs = 30000) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
        command.ExecuteNonQuery();

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
        await command.ExecuteNonQueryAsync(cancellationToken);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
