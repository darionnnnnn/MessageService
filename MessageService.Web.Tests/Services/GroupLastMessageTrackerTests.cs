using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class GroupLastMessageTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteMessageDbContext _dbContext;

    public GroupLastMessageTrackerTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(_connection)
            .Options;

        var cipherOptions = Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            Enabled = false,
            Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        });
        var cipher = new FieldCipher(cipherOptions, NullLogger<FieldCipher>.Instance);
        _dbContext = new SqliteMessageDbContext(options, cipher);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task TrackAsync_GroupDoesNotExist_AddsStubGroupAndSavesPointers()
    {
        var timestamp = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 100, timestamp, CancellationToken.None);

        var entries = _dbContext.ChangeTracker.Entries<Group>().ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(EntityState.Added, entry.State);
        Assert.Equal("G1", entry.Entity.GroupId);
        Assert.Equal(100, entry.Entity.LastMessageId);
        Assert.Equal(timestamp, entry.Entity.LastMessageAt);
        Assert.Equal(DateTimeOffset.MinValue, entry.Entity.UpdatedAt);

        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var savedGroup = await _dbContext.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == "G1");
        Assert.NotNull(savedGroup);
        Assert.Equal(100, savedGroup.LastMessageId);
        Assert.Equal(timestamp, savedGroup.LastMessageAt);
        Assert.Equal(DateTimeOffset.MinValue, savedGroup.UpdatedAt);
        Assert.Null(savedGroup.GroupName);
    }

    [Fact]
    public async Task TrackAsync_ExistingGroup_UpdatesPointersWithoutLoadingFullEntity()
    {
        var originalUpdated = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var oldTimestamp = new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.Zero);
        var newTimestamp = new DateTimeOffset(2026, 8, 16, 15, 30, 0, TimeSpan.Zero);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "G1",
            GroupName = "Original Group Name",
            PictureUrl = "https://example.com/pic.png",
            UpdatedAt = originalUpdated,
            LastMessageId = 50,
            LastMessageAt = oldTimestamp
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 100, newTimestamp, CancellationToken.None);

        var entries = _dbContext.ChangeTracker.Entries<Group>().ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(EntityState.Modified, entry.State);

        // 證明沒有載入整份 Group 實體：空殼 stub 的 GroupName 為 null
        Assert.Null(entry.Entity.GroupName);
        Assert.True(entry.Property(g => g.LastMessageId).IsModified);
        Assert.True(entry.Property(g => g.LastMessageAt).IsModified);
        Assert.False(entry.Property(g => g.GroupName).IsModified);
        Assert.False(entry.Property(g => g.UpdatedAt).IsModified);

        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var updatedGroup = await _dbContext.Groups.AsNoTracking().FirstAsync(g => g.GroupId == "G1");
        Assert.Equal("Original Group Name", updatedGroup.GroupName);
        Assert.Equal("https://example.com/pic.png", updatedGroup.PictureUrl);
        Assert.Equal(originalUpdated, updatedGroup.UpdatedAt);
        Assert.Equal(100, updatedGroup.LastMessageId);
        Assert.Equal(newTimestamp, updatedGroup.LastMessageAt);
    }

    [Fact]
    public async Task TrackAsync_OlderMessage_DoesNotUpdatePointersAndDoesNotTrackEntity()
    {
        var existingTimestamp = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var olderTimestamp = new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "G1",
            GroupName = "Group 1",
            UpdatedAt = DateTimeOffset.UtcNow,
            LastMessageId = 100,
            LastMessageAt = existingTimestamp
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 50, olderTimestamp, CancellationToken.None);

        // 較舊訊息不更新指標，且 ChangeTracker 不追蹤任何 Group 實體
        Assert.Empty(_dbContext.ChangeTracker.Entries<Group>());

        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var group = await _dbContext.Groups.AsNoTracking().FirstAsync(g => g.GroupId == "G1");
        Assert.Equal(100, group.LastMessageId);
        Assert.Equal(existingTimestamp, group.LastMessageAt);
    }

    [Fact]
    public async Task TrackAsync_ExistingGroupWithNullPointers_UpdatesPointers()
    {
        var timestamp = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "G1",
            GroupName = "Group 1",
            UpdatedAt = DateTimeOffset.UtcNow,
            LastMessageId = null,
            LastMessageAt = null
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 1, timestamp, CancellationToken.None);

        var entry = Assert.Single(_dbContext.ChangeTracker.Entries<Group>());
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.Null(entry.Entity.GroupName);

        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var group = await _dbContext.Groups.AsNoTracking().FirstAsync(g => g.GroupId == "G1");
        Assert.Equal(1, group.LastMessageId);
        Assert.Equal(timestamp, group.LastMessageAt);
        Assert.Equal("Group 1", group.GroupName);
    }

    [Fact]
    public async Task TrackAsync_MultipleCallsInSameDbContext_MaintainsLatestPointer()
    {
        var time1 = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var time2 = new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);
        var time3 = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        // 連續呼叫 TrackAsync：第一次會 Add stub，後續呼叫更新已追蹤的實體
        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 10, time1, CancellationToken.None);
        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 30, time3, CancellationToken.None);
        await GroupLastMessageTracker.TrackAsync(_dbContext, "G1", 20, time2, CancellationToken.None); // 較舊不應覆蓋 30

        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        var group = await _dbContext.Groups.AsNoTracking().FirstAsync(g => g.GroupId == "G1");
        Assert.Equal(30, group.LastMessageId);
        Assert.Equal(time3, group.LastMessageAt);
    }
}
