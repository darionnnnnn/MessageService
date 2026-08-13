using MessageService.Outbox;
using Microsoft.Data.Sqlite;

namespace MessageService.Tests.Outbox;

// OutboxDbContext 用 EnsureCreated()，既有的 outbox.db（Stage 1 就部署過的）升級到本次新增
// 的 DeadLetteredAt 欄位不會自動補上。這組測試模擬「舊版 schema 的檔案」，驗證啟動時的
// 補欄位邏輯正確且不破壞既有資料。
public class OutboxSchemaUpgraderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"outbox-schema-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>建一個模擬 Stage 1（沒有 DeadLetteredAt 欄位）的 Entries 表，塞一筆資料，
    /// 驗證升級後既有資料還在、且新欄位查得到（預設 null）。</summary>
    private void CreateLegacySchemaWithOneRow()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE Entries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WebhookEventId TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                Attempts INTEGER NOT NULL,
                NextAttemptAt INTEGER NULL,
                LastError TEXT NULL
            );
            INSERT INTO Entries (WebhookEventId, PayloadJson, CreatedAt, Attempts)
            VALUES ('evt-legacy', '{}', 0, 0);
            """;
        create.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureDeadLetterColumn_LegacySchemaMissingColumn_AddsColumn()
    {
        CreateLegacySchemaWithOneRow();

        OutboxSchemaUpgrader.EnsureDeadLetterColumn(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Entries)";
        using var reader = check.ExecuteReader();
        var columnNames = new List<string>();
        while (reader.Read())
        {
            columnNames.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        Assert.Contains("DeadLetteredAt", columnNames);
    }

    [Fact]
    public void EnsureDeadLetterColumn_LegacySchemaMissingColumn_PreservesExistingRow()
    {
        CreateLegacySchemaWithOneRow();

        OutboxSchemaUpgrader.EnsureDeadLetterColumn(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT WebhookEventId, DeadLetteredAt FROM Entries";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("evt-legacy", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(reader.Read()); // 只有這一筆，沒有被複製或弄丟
    }

    [Fact]
    public void EnsureDeadLetterColumn_ColumnAlreadyExists_IsNoOpAndDoesNotThrow()
    {
        CreateLegacySchemaWithOneRow();
        OutboxSchemaUpgrader.EnsureDeadLetterColumn(ConnectionString);

        // 再跑一次：模擬新版 EnsureCreated() 已經建好含該欄位的表、或本方法本身被重複呼叫
        var ex = Record.Exception(() => OutboxSchemaUpgrader.EnsureDeadLetterColumn(ConnectionString));

        Assert.Null(ex);
    }

    [Fact]
    public void EnableWalMode_SwitchesJournalModeToWal()
    {
        CreateLegacySchemaWithOneRow();

        OutboxSchemaUpgrader.EnableWalMode(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA journal_mode;";
        var mode = (string)query.ExecuteScalar()!;
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    /// <summary>模擬已經因為 LINE redelivery 而累積重複 WebhookEventId 的舊 outbox.db——
    /// P0 的根因：升級路徑必須先去重才能補建唯一索引，見 EnsureWebhookEventIdUniqueIndex 說明。</summary>
    private void CreateLegacySchemaWithDuplicateWebhookEventIds()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE Entries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WebhookEventId TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                Attempts INTEGER NOT NULL,
                NextAttemptAt INTEGER NULL,
                LastError TEXT NULL,
                DeadLetteredAt INTEGER NULL
            );
            INSERT INTO Entries (WebhookEventId, PayloadJson, CreatedAt, Attempts) VALUES ('evt-dup', '{"n":1}', 0, 0);
            INSERT INTO Entries (WebhookEventId, PayloadJson, CreatedAt, Attempts) VALUES ('evt-dup', '{"n":2}', 0, 0);
            INSERT INTO Entries (WebhookEventId, PayloadJson, CreatedAt, Attempts) VALUES ('evt-solo', '{"n":3}', 0, 0);
            """;
        create.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureWebhookEventIdUniqueIndex_DuplicateRows_KeepsOnlySmallestIdPerEvent()
    {
        CreateLegacySchemaWithDuplicateWebhookEventIds();

        OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Id, WebhookEventId, PayloadJson FROM Entries ORDER BY Id";
        using var reader = query.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("evt-dup", reader.GetString(1));
        Assert.Equal("""{"n":1}""", reader.GetString(2)); // 最小 Id 那筆保留

        Assert.True(reader.Read());
        Assert.Equal("evt-solo", reader.GetString(1));

        Assert.False(reader.Read());
    }

    [Fact]
    public void EnsureWebhookEventIdUniqueIndex_AfterDedupe_IndexRejectsFutureDuplicates()
    {
        CreateLegacySchemaWithDuplicateWebhookEventIds();

        OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex(ConnectionString);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Entries (WebhookEventId, PayloadJson, CreatedAt, Attempts) VALUES ('evt-solo', '{}', 0, 0)";

        var ex = Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void EnsureWebhookEventIdUniqueIndex_NoDuplicates_IsIdempotent()
    {
        CreateLegacySchemaWithOneRow();
        OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex(ConnectionString);

        var ex = Record.Exception(() => OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex(ConnectionString));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureDeadLetterColumn_FreshTableAlreadyHasColumn_IsNoOp()
    {
        // 模擬 EnsureCreated() 剛建好的全新資料庫：Entries 表一開始就含 DeadLetteredAt
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE Entries (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WebhookEventId TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    Attempts INTEGER NOT NULL,
                    NextAttemptAt INTEGER NULL,
                    LastError TEXT NULL,
                    DeadLetteredAt INTEGER NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        var ex = Record.Exception(() => OutboxSchemaUpgrader.EnsureDeadLetterColumn(ConnectionString));

        Assert.Null(ex);
    }
}
