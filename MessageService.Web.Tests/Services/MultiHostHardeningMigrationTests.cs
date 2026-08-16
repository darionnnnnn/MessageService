using MessageService.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MessageService.Tests.Services;

/// <summary>驗證 MultiHostHardening migration 正確新增 ClaimedAt 欄位與 MessageType 索引。</summary>
public class MultiHostHardeningMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"multi-host-hardening-test-{Guid.NewGuid():N}.db");
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
    public void Up_AddsClaimedAtColumn_AddsMessageTypeIndex_AndPreservesExistingData()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        // 1. 先套用到新 migration 的前一版（SplitBlobTables）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            var migrator = dbContext.Database.GetService<IMigrator>();
            migrator.Migrate("20260816065723_SplitBlobTables");
        }

        // 2. 用 raw SQL 塞入既有資料（GroupMessages + MessageContents 各一列）
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO GroupMessages (Id, WebhookEventId, LineMessageId, GroupId, UserId, MessageType, EventTimestamp, ReceivedAt)
                    VALUES (1, 'evt-mhh-1', 'line-mhh-1', 'G_TEST', 'U_TEST', 'sticker', 1000, '2026-08-16T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO MessageContents (Id, GroupMessageId, DownloadStatus, FailedAttempts)
                    VALUES (201, 1, 'Pending', 0);
                    """;
                cmd.ExecuteNonQuery();
            }
        }

        // 3. 套用最新 migration（MultiHostHardening）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();
        }

        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();

            // 斷言一：MessageContents 有 ClaimedAt 欄位，且既有列的值為 NULL
            var columns = ColumnNames(connection, "MessageContents");
            Assert.Contains("ClaimedAt", columns);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT ClaimedAt FROM MessageContents WHERE Id = 201;";
                var value = cmd.ExecuteScalar();
                // 既有列升級後 ClaimedAt 應為 NULL
                Assert.True(value == null || value == DBNull.Value,
                    $"既有列 ClaimedAt 應為 NULL，實際值：{value}");
            }

            // 斷言二：GroupMessages 上存在 MessageType 的索引
            // 用 PRAGMA index_list 確認索引存在，再用 PRAGMA index_info 確認索引欄位為 MessageType
            var indexExists = false;
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA index_list('GroupMessages');";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    var indexName = reader.GetString(reader.GetOrdinal("name"));
                    if (indexName == "IX_GroupMessages_MessageType")
                    {
                        indexExists = true;
                        break;
                    }
                }
            }
            Assert.True(indexExists, "GroupMessages 上應存在 IX_GroupMessages_MessageType 索引");

            // 進一步確認索引確實蓋了 MessageType 欄位
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA index_info('IX_GroupMessages_MessageType');";
                using var reader = pragma.ExecuteReader();
                Assert.True(reader.Read(), "IX_GroupMessages_MessageType 索引應至少有一個欄位");
                Assert.Equal("MessageType", reader.GetString(reader.GetOrdinal("name")));
            }

            // 斷言三：既有資料列（GroupMessages 與 MessageContents）升級後都還在
            Assert.Equal(1L, CountRows(connection, "SELECT COUNT(*) FROM GroupMessages;"));
            Assert.Equal(1L, CountRows(connection, "SELECT COUNT(*) FROM MessageContents WHERE Id = 201;"));
        }
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

    private static long CountRows(SqliteConnection connection, string query)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = query;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
