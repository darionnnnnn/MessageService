using MessageService.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MessageService.Tests.Services;

public class SplitBlobTablesMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"split-blob-migration-test-{Guid.NewGuid():N}.db");
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
    public void Up_SplitsBlobTables_BackfillsNonNullData_DropsOldColumns_AndEnforcesCascade()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        var groupPicBytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF, 0xFE };
        var memberPicBytes = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var messageContentBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x12 };

        // 1. 先套用到新 migration 的前一版（AnonymousLabelUnique）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            var migrator = dbContext.Database.GetService<IMigrator>();
            migrator.Migrate("20260816035025_AnonymousLabelUnique");
        }

        // 2. 用 raw SQL 塞入有 blob 與無 blob（NULL）的既有資料
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();

            // Groups: 一個有頭貼、一個沒頭貼（NULL）
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO Groups (GroupId, GroupName, PictureContent, UpdatedAt)
                    VALUES ($g1, 'Group With Pic', $pic1, 1000),
                           ($g2, 'Group Without Pic', NULL, 1000);
                    """;
                cmd.Parameters.AddWithValue("$g1", "G_WITH_PIC");
                cmd.Parameters.AddWithValue("$pic1", groupPicBytes);
                cmd.Parameters.AddWithValue("$g2", "G_NO_PIC");
                cmd.ExecuteNonQuery();
            }

            // GroupMembers: 一個有頭貼、一個沒頭貼（NULL）
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO GroupMembers (GroupId, UserId, DisplayName, PictureContent, UpdatedAt)
                    VALUES ($g1, $u1, 'Member With Pic', $pic2, 1000),
                           ($g1, $u2, 'Member Without Pic', NULL, 1000);
                    """;
                cmd.Parameters.AddWithValue("$g1", "G_WITH_PIC");
                cmd.Parameters.AddWithValue("$u1", "U_WITH_PIC");
                cmd.Parameters.AddWithValue("$pic2", memberPicBytes);
                cmd.Parameters.AddWithValue("$u2", "U_NO_PIC");
                cmd.ExecuteNonQuery();
            }

            // GroupMessages（MessageContents 的外鍵相依）
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO GroupMessages (Id, WebhookEventId, LineMessageId, GroupId, UserId, MessageType, EventTimestamp, ReceivedAt)
                    VALUES (1, 'evt-blob-1', 'line-msg-1', 'G_WITH_PIC', 'U_WITH_PIC', 'image', 1000, '2026-08-16T00:00:00Z'),
                           (2, 'evt-blob-2', 'line-msg-2', 'G_WITH_PIC', 'U_NO_PIC', 'text', 1000, '2026-08-16T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            // MessageContents: 一個有內容、一個沒內容（NULL）
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO MessageContents (Id, GroupMessageId, DownloadStatus, Content, FailedAttempts)
                    VALUES (101, 1, 'Completed', $content, 0),
                           (102, 2, 'Pending', NULL, 0);
                    """;
                cmd.Parameters.AddWithValue("$content", messageContentBytes);
                cmd.ExecuteNonQuery();
            }
        }

        // 3. 套用最新 migration（SplitBlobTables）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();

            // 4. 斷言三張新表各只有 1 列（NULL 的那些沒被搬過來），且搬過去的 bytes 內容完全相同
            var groupPics = dbContext.GroupPictures.ToList();
            Assert.Single(groupPics);
            Assert.Equal("G_WITH_PIC", groupPics[0].GroupId);
            Assert.Equal(groupPicBytes, groupPics[0].Content);

            var memberPics = dbContext.GroupMemberPictures.ToList();
            Assert.Single(memberPics);
            Assert.Equal("G_WITH_PIC", memberPics[0].GroupId);
            Assert.Equal("U_WITH_PIC", memberPics[0].UserId);
            Assert.Equal(memberPicBytes, memberPics[0].Content);

            var messageBlobs = dbContext.MessageContentBlobs.ToList();
            Assert.Single(messageBlobs);
            Assert.Equal(101, messageBlobs[0].MessageContentId);
            Assert.Equal(messageContentBytes, messageBlobs[0].Content);
        }

        // 4b. migration 建出來的 MessageContentBlobs 主鍵必須是 rowid 別名（INTEGER PRIMARY KEY）——
        // ContentStreamService／DbContentWorkSource 是用 SqliteBlob 以 MessageContentId 當 rowid
        // 開啟 blob 的。直接在 migration 產生的表上開一次，型別不對會在這裡炸而不是等到正式環境
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var blob = new SqliteBlob(connection, "MessageContentBlobs", "Content", 101, readOnly: true);
            var buffer = new byte[messageContentBytes.Length];
            Assert.Equal(buffer.Length, blob.Read(buffer, 0, buffer.Length));
            Assert.Equal(messageContentBytes, buffer);
        }

        // 5. 斷言父表上的舊欄位已經不存在
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            Assert.DoesNotContain("PictureContent", ColumnNames(connection, "Groups"));
            Assert.DoesNotContain("PictureContent", ColumnNames(connection, "GroupMembers"));
            Assert.DoesNotContain("Content", ColumnNames(connection, "MessageContents"));

            // 6. 斷言刪掉父表的列時，新表的對應列會被 CASCADE 一起刪掉
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            // (a) 刪除 MessageContent 101 -> MessageContentBlobs 101 被 CASCADE 刪除
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM MessageContents WHERE Id = 101;";
                delete.ExecuteNonQuery();
            }
            Assert.Equal(0, CountRows(connection, "SELECT COUNT(*) FROM MessageContentBlobs WHERE MessageContentId = 101;"));

            // (b) 刪除 GroupMember (G_WITH_PIC, U_WITH_PIC) -> GroupMemberPictures 被 CASCADE 刪除
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM GroupMembers WHERE GroupId = 'G_WITH_PIC' AND UserId = 'U_WITH_PIC';";
                delete.ExecuteNonQuery();
            }
            Assert.Equal(0, CountRows(connection, "SELECT COUNT(*) FROM GroupMemberPictures WHERE GroupId = 'G_WITH_PIC' AND UserId = 'U_WITH_PIC';"));

            // (c) 刪除 Group G_WITH_PIC -> GroupPictures 被 CASCADE 刪除
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM Groups WHERE GroupId = 'G_WITH_PIC';";
                delete.ExecuteNonQuery();
            }
            Assert.Equal(0, CountRows(connection, "SELECT COUNT(*) FROM GroupPictures WHERE GroupId = 'G_WITH_PIC';"));
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
