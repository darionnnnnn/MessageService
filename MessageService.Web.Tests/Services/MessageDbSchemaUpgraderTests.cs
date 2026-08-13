using MessageService.Models;
using MessageService.Services;
using Microsoft.Data.Sqlite;

namespace MessageService.Tests.Services;

// MessageDbContext 用 EnsureCreated()，既有的 messages.db（本輪 schema 變更之前就部署過的）
// 升級到新增的欄位／索引不會自動補上。這組測試模擬「舊版 schema 的檔案」，驗證啟動時的
// 補欄位／補索引邏輯正確且不破壞既有資料，比照 OutboxSchemaUpgraderTests 的手法。
public class MessageDbSchemaUpgraderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"messagedb-schema-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private void CreateLegacySchemaWithOneRow()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE GroupMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WebhookEventId TEXT NOT NULL,
                GroupId TEXT NOT NULL,
                EventTimestamp INTEGER NOT NULL
            );
            CREATE TABLE MessageContents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupMessageId INTEGER NOT NULL,
                DownloadStatus TEXT NOT NULL
            );
            CREATE TABLE ViewerSettings (
                Id INTEGER PRIMARY KEY,
                NameDisplayMode TEXT NOT NULL
            );
            INSERT INTO MessageContents (GroupMessageId, DownloadStatus) VALUES (1, 'Failed');
            INSERT INTO ViewerSettings (Id, NameDisplayMode) VALUES (1, 'MaskMiddle');
            """;
        create.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureSchema_LegacySchema_AddsAllNewColumns()
    {
        CreateLegacySchemaWithOneRow();

        MessageDbSchemaUpgrader.EnsureSchema(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        Assert.Equal(["Id", "GroupMessageId", "DownloadStatus", "FailedAttempts", "LastAttemptAt"],
            ColumnNames(connection, "MessageContents"));
        Assert.Equal(
            ["Id", "NameDisplayMode", "RetentionDays", "MaskNationalId", "MaskMobilePhone", "MaskLandline", "MaskNhiCard"],
            ColumnNames(connection, "ViewerSettings"));
    }

    [Fact]
    public void EnsureSchema_LegacySchema_PreservesExistingRows()
    {
        CreateLegacySchemaWithOneRow();

        MessageDbSchemaUpgrader.EnsureSchema(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT DownloadStatus, FailedAttempts, LastAttemptAt FROM MessageContents";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1)); // 新欄位預設值
        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void EnsureSchema_LegacySchema_NewViewerSettingsColumnsDefaultToRetainingBehavior()
    {
        CreateLegacySchemaWithOneRow();

        MessageDbSchemaUpgrader.EnsureSchema(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT RetentionDays, MaskNationalId, MaskMobilePhone, MaskLandline, MaskNhiCard FROM ViewerSettings";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(ViewerSettings.DefaultRetentionDays, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1)); // PII 遮蔽開關預設全開
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(1, reader.GetInt32(4));
    }

    [Fact]
    public void EnsureSchema_LegacySchema_CreatesGroupIdIndexes()
    {
        CreateLegacySchemaWithOneRow();

        MessageDbSchemaUpgrader.EnsureSchema(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'GroupMessages'";
        using var reader = query.ExecuteReader();
        var indexNames = new List<string>();
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }
        Assert.Contains("IX_GroupMessages_GroupId_Id", indexNames);
        Assert.Contains("IX_GroupMessages_GroupId_EventTimestamp", indexNames);
    }

    [Fact]
    public void EnsureSchema_AlreadyUpgraded_IsNoOpAndDoesNotThrow()
    {
        CreateLegacySchemaWithOneRow();
        MessageDbSchemaUpgrader.EnsureSchema(ConnectionString);

        var ex = Record.Exception(() => MessageDbSchemaUpgrader.EnsureSchema(ConnectionString));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureSchema_FreshSchemaAlreadyHasColumns_IsNoOp()
    {
        // 模擬 EnsureCreated() 剛建好的全新資料庫：欄位一開始就存在
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE GroupMessages (Id INTEGER PRIMARY KEY, GroupId TEXT NOT NULL, EventTimestamp INTEGER NOT NULL);
                CREATE TABLE MessageContents (
                    Id INTEGER PRIMARY KEY, DownloadStatus TEXT NOT NULL,
                    FailedAttempts INTEGER NOT NULL DEFAULT 0, LastAttemptAt INTEGER NULL
                );
                CREATE TABLE ViewerSettings (
                    Id INTEGER PRIMARY KEY, NameDisplayMode TEXT NOT NULL,
                    RetentionDays INTEGER NOT NULL DEFAULT 1095,
                    MaskNationalId INTEGER NOT NULL DEFAULT 1, MaskMobilePhone INTEGER NOT NULL DEFAULT 1,
                    MaskLandline INTEGER NOT NULL DEFAULT 1, MaskNhiCard INTEGER NOT NULL DEFAULT 1
                );
                """;
            create.ExecuteNonQuery();
        }

        var ex = Record.Exception(() => MessageDbSchemaUpgrader.EnsureSchema(ConnectionString));

        Assert.Null(ex);
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
        return names;
    }
}
