using MessageService.Data;
using MessageService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MessageService.Tests.Services;

public class AnonymousLabelUniqueMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"anon-migration-test-{Guid.NewGuid():N}.db");
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
    public void Up_RepairsDuplicateLabels_AndEnforcesUniqueIndex()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        // 1. 先套用到新 migration 的前一版（AvatarSupport）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            var migrator = dbContext.Database.GetService<IMigrator>();
            migrator.Migrate("20260814124930_AvatarSupport");

            // 2. 塞入同群組、同 Label、不同 UserId 的重複資料
            var baseTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            dbContext.AnonymousIdentities.AddRange(
                new AnonymousIdentity
                {
                    GroupId = "G_TEST",
                    UserId = "U1",
                    IconKey = "icon_cat",
                    Label = "柴犬",
                    AssignedAt = baseTime,
                },
                new AnonymousIdentity
                {
                    GroupId = "G_TEST",
                    UserId = "U2",
                    IconKey = "icon_cat",
                    Label = "柴犬",
                    AssignedAt = baseTime.AddMinutes(1),
                },
                new AnonymousIdentity
                {
                    GroupId = "G_TEST",
                    UserId = "U3",
                    IconKey = "icon_cat",
                    Label = "柴犬",
                    AssignedAt = baseTime.AddMinutes(2),
                },
                new AnonymousIdentity
                {
                    GroupId = "G_OTHER",
                    UserId = "U4",
                    IconKey = "icon_cat",
                    Label = "柴犬",
                    AssignedAt = baseTime,
                });
            dbContext.SaveChanges();
        }

        // 3. 套用最新 migration（包含修補邏輯與唯一索引）
        using (var dbContext = new SqliteMessageDbContext(options))
        {
            dbContext.Database.Migrate();

            // 4. 斷言同群組重複的 Label 變成互不相同（第一筆維持原值、第二筆起帶括號序號），不同群組不互相干擾
            var groupIdentities = dbContext.AnonymousIdentities
                .Where(a => a.GroupId == "G_TEST")
                .AsEnumerable()
                .OrderBy(a => a.AssignedAt)
                .ToList();

            Assert.Equal(3, groupIdentities.Count);
            Assert.Equal("U1", groupIdentities[0].UserId);
            Assert.Equal("柴犬", groupIdentities[0].Label);
            Assert.Equal("U2", groupIdentities[1].UserId);
            Assert.Equal("柴犬 (2)", groupIdentities[1].Label);
            Assert.Equal("U3", groupIdentities[2].UserId);
            Assert.Equal("柴犬 (3)", groupIdentities[2].Label);

            var otherGroup = dbContext.AnonymousIdentities
                .Single(a => a.GroupId == "G_OTHER");
            Assert.Equal("柴犬", otherGroup.Label);

            // 5. 斷言唯一索引確實存在（嘗試再插入同 (GroupId, Label) 會失敗）
            dbContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = "G_TEST",
                UserId = "U_NEW",
                IconKey = "icon_dog",
                Label = "柴犬",
                AssignedAt = DateTimeOffset.UtcNow,
            });

            Assert.Throws<DbUpdateException>(() => dbContext.SaveChanges());
        }
    }
}
