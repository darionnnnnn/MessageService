using Microsoft.Data.Sqlite;

namespace MessageService.Outbox;

/// <summary>OutboxDbContext 用 EnsureCreated()、不像主資料庫走正式 EF migration——outbox 是
/// 收錄端本機的緩衝，不值得為它另建一套 migration 機制。但 EnsureCreated() 只在資料庫檔案
/// 完全不存在時建表：Stage 1 就已經部署過的既有 outbox.db 升級到本次新增的 DeadLetteredAt
/// 欄位不會自動補上，缺欄位會讓後續每一次查詢都直接炸掉。啟動時用 PRAGMA table_info 檢查、
/// 缺什麼補什麼，比照 StickerId 那次對 messages.db 的處理方式（見 README 的 GroupMessages
/// 欄位表附註）。必須在 EnsureCreated() 之後呼叫，確保 Entries 資料表本身已經存在。</summary>
public static class OutboxSchemaUpgrader
{
    public static void EnsureDeadLetterColumn(string connectionString, int busyTimeoutMs = 30000)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        SetBusyTimeout(connection, busyTimeoutMs);

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(Entries)";
            using var reader = check.ExecuteReader();
            var nameOrdinal = -1;
            while (reader.Read())
            {
                if (nameOrdinal < 0)
                {
                    nameOrdinal = reader.GetOrdinal("name");
                }
                if (reader.GetString(nameOrdinal) == nameof(OutboxEntry.DeadLetteredAt))
                {
                    return; // 已經有這個欄位，不必補（新建的資料庫走 EnsureCreated() 就已經含它）
                }
            }
        }

        using var alter = connection.CreateCommand();
        // DateTimeOffsetToBinaryConverter 底層存的是 long（ToBinary()），對應 SQLite 的 INTEGER 型別
        alter.CommandText = "ALTER TABLE Entries ADD COLUMN DeadLetteredAt INTEGER NULL";
        alter.ExecuteNonQuery();
    }

    /// <summary>outbox.db 是 webhook 執行緒寫、forwarder 執行緒讀刪，預設 rollback journal
    /// 模式下兩邊會互相 block。連線開啟時由 Database:SqliteBusyTimeoutMs 明確設定 busy_timeout（預設 30 秒），
    /// WAL 是資料庫檔案的持久屬性，設一次即可，之後每次開啟連線都會沿用。</summary>
    public static void EnableWalMode(string connectionString, int busyTimeoutMs = 30000)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        SetBusyTimeout(connection, busyTimeoutMs);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }

    /// <summary>OutboxDbContext 的 WebhookEventId 唯一索引只對 EnsureCreated() 全新建立的檔案
    /// 生效——既有 outbox.db 可能已經因為 LINE redelivery 而累積了重複列（P0：這正是批次 ingest
    /// 端點在重複鍵上直接 500、讓 Edge outbox 永久卡死的根因，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md
    /// 批次A）。升級路徑必須先清掉重複列才能建索引，否則 CREATE UNIQUE INDEX 在既有重複資料上
    /// 會直接失敗，讓已經卡死的現場升級後更起不來。保留每組重複列裡 Id 最小的一筆，跟
    /// OutboxForwarderService 用 OrderBy(Id) 挑批次的既有語意一致（先寫入的先處理）。</summary>
    public static void EnsureWebhookEventIdUniqueIndex(string connectionString, int busyTimeoutMs = 30000)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        SetBusyTimeout(connection, busyTimeoutMs);

        using (var dedupe = connection.CreateCommand())
        {
            dedupe.CommandText =
                "DELETE FROM Entries WHERE Id NOT IN (SELECT MIN(Id) FROM Entries GROUP BY WebhookEventId)";
            dedupe.ExecuteNonQuery();
        }

        using var createIndex = connection.CreateCommand();
        createIndex.CommandText =
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Entries_WebhookEventId ON Entries(WebhookEventId)";
        createIndex.ExecuteNonQuery();
    }

    private static void SetBusyTimeout(SqliteConnection connection, int busyTimeoutMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs};";
        command.ExecuteNonQuery();
    }
}
