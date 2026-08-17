using MessageService.Data;
using MessageService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MessageService.Tests.Services;

/// <summary>驗證 GroupMessages.MessageType 索引套用 sticker 篩選條件（SQLite 與 SQL Server 兩套模型與 migration）。</summary>
public class FilterMessageTypeIndexMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"filter-msgtype-migration-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Sqlite_Model_MessageTypeIndex_HasStickerFilter()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new SqliteMessageDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(GroupMessage));
        Assert.NotNull(entityType);

        var property = entityType.FindProperty(nameof(GroupMessage.MessageType));
        Assert.NotNull(property);

        var index = entityType.FindIndex(property);
        Assert.NotNull(index);
        Assert.Equal("\"MessageType\" = 'sticker'", index.GetFilter());
    }

    [Fact]
    public void SqlServer_Model_MessageTypeIndex_HasStickerFilter()
    {
        var options = new DbContextOptionsBuilder<SqlServerMessageDbContext>()
            .UseSqlServer("Server=(local);Database=consistency-check-only;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        using var dbContext = new SqlServerMessageDbContext(options);

        var entityType = dbContext.Model.FindEntityType(typeof(GroupMessage));
        Assert.NotNull(entityType);

        var property = entityType.FindProperty(nameof(GroupMessage.MessageType));
        Assert.NotNull(property);

        var index = entityType.FindIndex(property);
        Assert.NotNull(index);
        Assert.Equal("[MessageType] = 'sticker'", index.GetFilter());
    }

    [Fact]
    public void Up_And_Down_MigratesMessageTypeIndex_WithFilter_AndPreservesData()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        // 1. 先套用到新 migration 的前一版（MultiHostHardening）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            var migrator = dbContext.Database.GetService<IMigrator>();
            migrator.Migrate("20260816115133_MultiHostHardening");
        }

        // 2. 塞入測試資料（一筆 sticker 訊息、一筆 text 訊息）
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO GroupMessages (Id, WebhookEventId, LineMessageId, GroupId, UserId, MessageType, EventTimestamp, ReceivedAt)
                VALUES (1, 'evt-fmi-1', 'line-fmi-1', 'G_TEST', 'U_TEST', 'sticker', 1000, '2026-08-17T00:00:00Z'),
                       (2, 'evt-fmi-2', 'line-fmi-2', 'G_TEST', 'U_TEST', 'text', 1001, '2026-08-17T00:00:01Z');
                """;
            cmd.ExecuteNonQuery();
        }

        // 3. 套用最新 migration（FilterMessageTypeIndex）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();
        }

        // 4. 斷言 SQLite 中該索引的 SQL 包含篩選條件
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_GroupMessages_MessageType';";
            var sql = (string?)cmd.ExecuteScalar();
            Assert.NotNull(sql);
            Assert.Contains("\"MessageType\" = 'sticker'", sql);

            // 斷言既有資料列皆完整保留
            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM GroupMessages;";
            Assert.Equal(2L, (long)(countCmd.ExecuteScalar() ?? 0L));
        }

        // 5. 測試 Down：還原回 MultiHostHardening
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            var migrator = dbContext.Database.GetService<IMigrator>();
            migrator.Migrate("20260816115133_MultiHostHardening");
        }

        // 6. 斷言 SQLite 中該索引已還原為無篩選索引
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_GroupMessages_MessageType';";
            var sql = (string?)cmd.ExecuteScalar();
            Assert.NotNull(sql);
            Assert.DoesNotContain("WHERE", sql);

            // 斷言資料仍然完整
            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM GroupMessages;";
            Assert.Equal(2L, (long)(countCmd.ExecuteScalar() ?? 0L));
        }
    }
}
