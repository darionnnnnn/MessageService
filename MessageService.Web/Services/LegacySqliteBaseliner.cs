using MessageService.Data;
using MessageService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MessageService.Services;

/// <summary>把「用 EnsureCreated() 建立、從沒有 migrations 歷史紀錄」的既有 SQLite 檔案
/// 一次性橋接到 InitialCreate baseline，橋接完之後交給 <c>Database.Migrate()</c> 接手——
/// 既有部署升級不需要手動刪檔重建。
///
/// 舊版 MessageDbSchemaUpgrader 只補了 SchemaHardeningRound1 那批（FailedAttempts／
/// LastAttemptAt／RetentionDays／四個遮蔽開關／兩個索引），沒補到再更早兩批新增的
/// StickerId／PackageId 欄位與 AnonymousIdentities 整張表——舊檔案直接連線會因缺欄位
/// 噴 SQL 錯誤，這裡把三批缺漏一次補齊。</summary>
public static class LegacySqliteBaseliner
{
    private const string BaselineMigrationId = "20260813045134_InitialCreate";
    private const string ProductVersion = "10.0.10";

    public static void EnsureBaseline(string connectionString, ILogger logger)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        if (!TableExists(connection, "GroupMessages"))
        {
            return; // 全新檔案，交給 Database.Migrate() 從頭建表，不需要橋接
        }

        if (TableExists(connection, "__EFMigrationsHistory"))
        {
            return; // 已經橋接過，或本來就是用 Migrate() 建的
        }

        logger.LogInformation(
            "偵測到既有 SQLite 檔案沒有 migrations 歷史紀錄，開始一次性橋接到 InitialCreate baseline...");

        BackfillMissingColumnsAndTables(connection);
        MarkInitialCreateAsApplied(connectionString, connection);

        logger.LogInformation("既有 SQLite 檔案橋接完成，之後交給 Database.Migrate() 接手。");
    }

    /// <summary>補齊三批各自獨立時期新增、舊 upgrader 只補了其中一批的欄位／表。
    /// 用「欄位／表已存在就跳過」的方式寫，所以就算某個檔案剛好已經有其中幾批，
    /// 重複呼叫也安全。</summary>
    private static void BackfillMissingColumnsAndTables(SqliteConnection connection)
    {
        // SchemaHardeningRound1（舊 MessageDbSchemaUpgrader 原本補的那批）
        EnsureColumn(connection, "MessageContents", "FailedAttempts", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "MessageContents", "LastAttemptAt", "INTEGER NULL");
        EnsureColumn(connection, "ViewerSettings", "RetentionDays", $"INTEGER NOT NULL DEFAULT {ViewerSettings.DefaultRetentionDays}");
        EnsureColumn(connection, "ViewerSettings", "MaskNationalId", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskMobilePhone", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskLandline", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "ViewerSettings", "MaskNhiCard", "INTEGER NOT NULL DEFAULT 1");
        EnsureIndex(connection, "IX_GroupMessages_GroupId_Id", "GroupMessages", "GroupId, Id");
        EnsureIndex(connection, "IX_GroupMessages_GroupId_EventTimestamp", "GroupMessages", "GroupId, EventTimestamp");

        // AddStickerIdAndPackageId（舊 upgrader 沒補到，缺欄位查詢會直接 SQL error）
        EnsureColumn(connection, "GroupMessages", "StickerId", "TEXT NULL");
        EnsureColumn(connection, "GroupMessages", "PackageId", "TEXT NULL");

        // AddAnonymousIdentityAndAnonymousMode（舊 upgrader 也沒補到，README 只能請使用者
        // 手動刪檔重建——這裡改成自動補表）
        EnsureAnonymousIdentitiesTable(connection);
    }

    /// <summary>寫入 __EFMigrationsHistory，告訴 EF「InitialCreate 已經套用過」——用
    /// IHistoryRepository 產生正確的建表／插入 SQL，而不是自己手刻，避免跟 EF 內部實際
    /// 期待的欄位型別／限制式有出入。</summary>
    private static void MarkInitialCreateAsApplied(string connectionString, SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>().UseSqlite(connectionString).Options;
        using var dbContext = new SqliteMessageDbContext(options);
        var historyRepository = dbContext.GetService<IHistoryRepository>();

        ExecuteRaw(connection, historyRepository.GetCreateIfNotExistsScript());
        ExecuteRaw(connection, historyRepository.GetInsertScript(new HistoryRow(BaselineMigrationId, ProductVersion)));
    }

    private static void EnsureAnonymousIdentitiesTable(SqliteConnection connection)
    {
        if (TableExists(connection, "AnonymousIdentities"))
        {
            return;
        }

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE "AnonymousIdentities" (
                "GroupId" TEXT NOT NULL,
                "UserId" TEXT NOT NULL,
                "IconKey" TEXT NOT NULL,
                "Label" TEXT NOT NULL,
                "AssignedAt" TEXT NOT NULL,
                CONSTRAINT "PK_AnonymousIdentities" PRIMARY KEY ("GroupId", "UserId")
            );
            """;
        create.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        check.Parameters.AddWithValue("$name", table);
        return check.ExecuteScalar() is not null;
    }

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
