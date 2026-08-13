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
    public static void EnsureDeadLetterColumn(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

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
    /// 模式下兩邊會互相 block（busy_timeout 預設 30 秒，遠超 LINE 的 webhook 逾時）。
    /// WAL 是資料庫檔案的持久屬性，設一次即可，之後每次開啟連線都會沿用。</summary>
    public static void EnableWalMode(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }
}
