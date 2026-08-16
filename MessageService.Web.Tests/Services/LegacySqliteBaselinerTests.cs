using MessageService.Data;
using MessageService.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

// 既有 SQLite 檔案（EnsureCreated() 建的、從沒有 migrations 歷史紀錄）升級到 Database.Migrate()
// 路徑的橋接邏輯。核心主張：橋接後拿同一個檔案跑 Migrate() 是全 no-op，且跟一個從空白直接
// Migrate() 出來的新檔案 schema 完全一致——這是這批改動最重要的正確性保證，比對兩邊而不是
// 只驗證「沒有丟例外」。
public class LegacySqliteBaselinerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"legacy-baseline-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>用 EnsureCreated() 建出「目前完整模型」的資料庫，再手動砍掉三批較晚才加入的
    /// 欄位／表，模擬「schema 停在很久以前」的既有檔案——比手刻全部 CREATE TABLE 更貼近真實
    /// 情況：其餘表（Groups／GroupMembers／MaskKeywords...）維持跟現在完全一樣，只有真正
    /// 要測的三批缺漏被移除。</summary>
    private void CreateLegacyDatabaseWithData()
    {
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(ConnectionString).Options;
        using (var dbContext = new MessageDbContext(options))
        {
            dbContext.Database.EnsureCreated();
            dbContext.GroupMessages.Add(new Models.GroupMessage
            {
                WebhookEventId = "evt-legacy-1",
                LineMessageId = "m1",
                GroupId = "G1",
                UserId = "U1",
                MessageType = "text",
                Text = "既有訊息",
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            dbContext.SaveChanges();
        }

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE AnonymousIdentities;
            DROP TABLE HostHeartbeats;
            DROP TABLE GroupPictures;
            DROP TABLE GroupMemberPictures;
            DROP TABLE MessageContentBlobs;
            ALTER TABLE GroupMessages DROP COLUMN StickerId;
            ALTER TABLE GroupMessages DROP COLUMN PackageId;
            ALTER TABLE MessageContents DROP COLUMN FailedAttempts;
            ALTER TABLE MessageContents DROP COLUMN LastAttemptAt;
            ALTER TABLE ViewerSettings DROP COLUMN RetentionDays;
            ALTER TABLE ViewerSettings DROP COLUMN MaskNationalId;
            ALTER TABLE ViewerSettings DROP COLUMN MaskMobilePhone;
            ALTER TABLE ViewerSettings DROP COLUMN MaskLandline;
            ALTER TABLE ViewerSettings DROP COLUMN MaskNhiCard;
            ALTER TABLE Groups DROP COLUMN LastMessageId;
            ALTER TABLE Groups DROP COLUMN LastMessageAt;
            ALTER TABLE Groups DROP COLUMN PictureContentType;
            ALTER TABLE Groups DROP COLUMN PictureFetchedUrl;
            ALTER TABLE Groups DROP COLUMN PictureUpdatedAt;
            ALTER TABLE GroupMembers DROP COLUMN PictureContentType;
            ALTER TABLE GroupMembers DROP COLUMN PictureFetchedUrl;
            ALTER TABLE GroupMembers DROP COLUMN PictureUpdatedAt;
            DROP INDEX IF EXISTS IX_GroupMessages_GroupId_Id;
            DROP INDEX IF EXISTS IX_GroupMessages_GroupId_EventTimestamp;
            DROP INDEX IF EXISTS IX_MessageContents_DownloadStatus;
            """;
        drop.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureBaseline_FreshFile_DoesNothing()
    {
        // 檔案完全不存在／沒有 GroupMessages 表——交給 Database.Migrate() 從頭建表，
        // 橋接邏輯不該動手（SqliteConnection.Open() 本身就會建立空檔案，不代表建了任何 schema，
        // 所以這裡只驗證沒有拋例外，不驗證檔案存在與否）
        var ex = Record.Exception(() => LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureBaseline_AlreadyMigrated_IsNoOp()
    {
        // 已經走 Migrate() 建出來的資料庫（有 history 表）——不該被誤判成需要橋接
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>().UseSqlite(ConnectionString).Options;
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();
        }

        var ex = Record.Exception(() => LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureBaseline_LegacySchema_AddsAllMissingColumnsAndTables()
    {
        CreateLegacyDatabaseWithData();

        LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        Assert.Contains("StickerId", ColumnNames(connection, "GroupMessages"));
        Assert.Contains("PackageId", ColumnNames(connection, "GroupMessages"));
        Assert.Contains("FailedAttempts", ColumnNames(connection, "MessageContents"));
        Assert.Contains("LastAttemptAt", ColumnNames(connection, "MessageContents"));
        Assert.Equal(
            new[] { "Id", "NameDisplayMode", "RetentionDays", "MaskNationalId", "MaskMobilePhone", "MaskLandline", "MaskNhiCard" }
                .OrderBy(x => x, StringComparer.Ordinal),
            ColumnNames(connection, "ViewerSettings"));
        Assert.True(TableExists(connection, "AnonymousIdentities"));
        Assert.Contains("IX_GroupMessages_GroupId_Id", IndexNames(connection, "GroupMessages"));
        Assert.Contains("IX_GroupMessages_GroupId_EventTimestamp", IndexNames(connection, "GroupMessages"));
    }

    [Fact]
    public void EnsureBaseline_LegacySchema_PreservesExistingData()
    {
        CreateLegacyDatabaseWithData();

        LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance);

        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(ConnectionString).Options;
        using var dbContext = new MessageDbContext(options);
        var message = Assert.Single(dbContext.GroupMessages);
        Assert.Equal("evt-legacy-1", message.WebhookEventId);
        Assert.Equal("既有訊息", message.Text);
        Assert.Null(message.StickerId); // 新欄位補上但沒有值可回填，維持 null
    }

    [Fact]
    public void EnsureBaseline_LegacySchema_CreatesMigrationsHistoryTableWithInitialCreateRow()
    {
        CreateLegacyDatabaseWithData();

        LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        Assert.True(TableExists(connection, "__EFMigrationsHistory"));
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.EndsWith("_InitialCreate", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void EnsureBaseline_ThenMigrate_ReportsNoPendingMigrations()
    {
        CreateLegacyDatabaseWithData();
        LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance);

        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>().UseSqlite(ConnectionString).Options;
        using var dbContext = new SqliteMessageDbContext(options);
        var ex = Record.Exception(() => dbContext.Database.Migrate());

        Assert.Null(ex);
        Assert.Empty(dbContext.Database.GetPendingMigrations());
        // 橋接只標記 InitialCreate 為已套用；InitialCreate 之後新增的 migration（例如
        // AddGroupLastMessageTracking）仍應該由 Migrate() 正常套用，不是「全部視為已套用」
        Assert.Contains("InitialCreate", string.Join(",", dbContext.Database.GetAppliedMigrations()));
    }

    [Fact]
    public void EnsureBaseline_ThenMigrate_ProducesSameSchemaAsFreshMigrate()
    {
        // 這是整個橋接機制最重要的保證：舊檔案橋接完的最終 schema，要跟全新資料庫直接
        // Database.Migrate() 出來的 schema 完全一致——不是「看起來能動」，是逐表逐欄位比對。
        CreateLegacyDatabaseWithData();
        LegacySqliteBaseliner.EnsureBaseline(ConnectionString, NullLogger.Instance);
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>().UseSqlite(ConnectionString).Options;
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();
        }

        var freshDbPath = Path.Combine(Path.GetTempPath(), $"legacy-baseline-fresh-{Guid.NewGuid():N}.db");
        try
        {
            var freshOptions = new DbContextOptionsBuilder<SqliteMessageDbContext>()
                .UseSqlite($"Data Source={freshDbPath}").Options;
            using (var freshDbContext = new SqliteMessageDbContext(freshOptions))
            {
                freshDbContext.Database.Migrate();
            }

            using var legacyConnection = new SqliteConnection(ConnectionString);
            legacyConnection.Open();
            using var freshConnection = new SqliteConnection($"Data Source={freshDbPath}");
            freshConnection.Open();

            foreach (var table in new[]
                     {
                         "GroupMessages", "MessageContents", "Groups", "GroupMembers", "ViewerSettings",
                         "MaskKeywords", "MaskKeywordGroups", "UserAliases", "AnonymousIdentities",
                     })
            {
                Assert.Equal(ColumnNames(freshConnection, table), ColumnNames(legacyConnection, table));
            }
            Assert.Equal(IndexNames(freshConnection, "GroupMessages"), IndexNames(legacyConnection, "GroupMessages"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(freshDbPath))
            {
                File.Delete(freshDbPath);
            }
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        check.Parameters.AddWithValue("$name", table);
        return check.ExecuteScalar() is not null;
    }

    private static List<string> ColumnNames(SqliteConnection connection, string table)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static List<string> IndexNames(SqliteConnection connection, string table)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table";
        query.Parameters.AddWithValue("$table", table);
        using var reader = query.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
