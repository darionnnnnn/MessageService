using Microsoft.Data.Sqlite;

namespace MessageService.Services;

/// <summary>主資料庫（messages.db）用 EnsureCreated()，同樣只在檔案完全不存在時建表——
/// 既有的本機資料庫升級到本輪新增的欄位／索引不會自動套用，比照 OutboxSchemaUpgrader
/// 對 outbox.db 的處理方式。SQLite 對宣告型別的長度沒有實際約束（型別親和性），
/// 所以 GroupId/UserId/MessageType/DownloadStatus 的 nvarchar(64)/(20) 收斂在 SQLite
/// 上不需要真的 ALTER 欄位型別，只有新欄位與新索引需要補。</summary>
public static class MessageDbSchemaUpgrader
{
    public static void EnsureSchema(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        EnsureColumn(connection, "MessageContents", "FailedAttempts", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "MessageContents", "LastAttemptAt", "INTEGER NULL");
        EnsureColumn(connection, "ViewerSettings", "RetentionDays", $"INTEGER NOT NULL DEFAULT {Models.ViewerSettings.DefaultRetentionDays}");
        EnsureColumn(connection, "ViewerSettings", "MaskNationalId", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskMobilePhone", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskLandline", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskNhiCard", "INTEGER NOT NULL DEFAULT 1");

        EnsureIndex(connection, "IX_GroupMessages_GroupId_Id", "GroupMessages", "GroupId, Id");
        EnsureIndex(connection, "IX_GroupMessages_GroupId_EventTimestamp", "GroupMessages", "GroupId, EventTimestamp");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            var nameOrdinal = -1;
            while (reader.Read())
            {
                if (nameOrdinal < 0)
                {
                    nameOrdinal = reader.GetOrdinal("name");
                }
                if (string.Equals(reader.GetString(nameOrdinal), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
        alter.ExecuteNonQuery();
    }

    private static void EnsureIndex(SqliteConnection connection, string indexName, string table, string columns)
    {
        using var create = connection.CreateCommand();
        create.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {table} ({columns})";
        create.ExecuteNonQuery();
    }
}
