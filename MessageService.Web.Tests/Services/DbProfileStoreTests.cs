using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class DbProfileStoreTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly SqliteMessageDbContext _dbContext;

    public DbProfileStoreTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(_connection)
            .Options;

        var cipher = CreateCipher(false);
        _dbContext = new SqliteMessageDbContext(options, cipher);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private FieldCipher CreateCipher(bool enabled)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EncryptionOptions
        {
            Enabled = enabled,
            Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        });
        return new FieldCipher(options, NullLogger<FieldCipher>.Instance);
    }

    [Fact]
    public async Task UpsertGroupAsync_WithPictureBytes_WritesContentAndMetadata()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var pictureBytes = new byte[] { 1, 2, 3 };
        var summary = new GroupSummary("g1", "Group 1", "https://example.com/pic", pictureBytes, "image/jpeg");

        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        var group = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(group);
        Assert.Equal(pictureBytes, group.Picture?.Content);
        Assert.Equal("image/jpeg", group.PictureContentType);
        Assert.Equal("https://example.com/pic", group.PictureFetchedUrl);
        Assert.NotNull(group.PictureUpdatedAt);
    }

    [Fact]
    public async Task UpsertGroupAsync_WithoutPictureBytes_DoesNotClearExistingPicture()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var pictureBytes = new byte[] { 1, 2, 3 };
        var summary1 = new GroupSummary("g1", "Group 1", "https://example.com/pic", pictureBytes, "image/jpeg");
        await store.UpsertGroupAsync("g1", summary1, CancellationToken.None);

        var summary2 = new GroupSummary("g1", "Group 1", "https://example.com/pic2", null, null);
        await store.UpsertGroupAsync("g1", summary2, CancellationToken.None);

        var group = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(group);
        Assert.Equal(pictureBytes, group.Picture?.Content);
        Assert.Equal("image/jpeg", group.PictureContentType);
        Assert.Equal("https://example.com/pic", group.PictureFetchedUrl);
    }

    [Fact]
    public async Task UpsertGroupAsync_ExistingGroupWithPicture_WithoutPictureBytes_PreservesPictureAndMetadataAndDoesNotTrackBlob()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var originalTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Original Group",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = originalTime,
            PictureContentType = "image/jpeg",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureUpdatedAt = originalTime,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3, 4] }
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var summary = new GroupSummary("g1", "Updated Group", "https://example.com/pic2", null, null);
        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        // 驗證 ChangeTracker 完全沒有載入或追蹤 GroupPicture
        Assert.Empty(_dbContext.ChangeTracker.Entries<GroupPicture>());

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(reloaded);
        Assert.Equal("Updated Group", reloaded.GroupName);
        Assert.Equal("https://example.com/pic2", reloaded.PictureUrl);
        Assert.Equal("image/jpeg", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic1", reloaded.PictureFetchedUrl);
        Assert.Equal(originalTime, reloaded.PictureUpdatedAt);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupPictures.CountAsync(p => p.GroupId == "g1"));
    }

    [Fact]
    public async Task UpsertGroupAsync_ExistingGroupWithPicture_WithNewPictureBytes_UpdatesPictureWithoutDuplicates()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            PictureContentType = "image/jpeg",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureUpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3] }
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var newBytes = new byte[] { 9, 8, 7, 6 };
        var summary = new GroupSummary("g1", "Group 1 New", "https://example.com/pic2", newBytes, "image/png");
        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(reloaded);
        Assert.Equal("Group 1 New", reloaded.GroupName);
        Assert.Equal("https://example.com/pic2", reloaded.PictureUrl);
        Assert.Equal("image/png", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic2", reloaded.PictureFetchedUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(newBytes, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupPictures.CountAsync(p => p.GroupId == "g1"));
    }

    [Fact]
    public async Task UpsertGroupAsync_ExistingGroupWithoutPicture_WithPictureBytes_InsertsPicture()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var pictureBytes = new byte[] { 5, 6, 7 };
        var summary = new GroupSummary("g1", "Group 1", "https://example.com/pic1", pictureBytes, "image/webp");
        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(reloaded);
        Assert.Equal("image/webp", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic1", reloaded.PictureFetchedUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(pictureBytes, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupPictures.CountAsync(p => p.GroupId == "g1"));
    }

    [Fact]
    public async Task UpsertGroupAsync_ConcurrentInsert_RetriesAndUpdatesAsExpected()
    {
        var interceptor = new SaveFailureInterceptor();
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        var cipher = CreateCipher(false);
        using var dbContext = new SqliteMessageDbContext(options, cipher);
        var store = new DbProfileStore(dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        interceptor.BeforeSaveOnce = async () =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Groups (GroupId, GroupName, UpdatedAt) VALUES ('g-concurrent', 'Concurrent Group', '2026-08-16T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        };

        var pictureBytes = new byte[] { 101, 102, 103 };
        var summary = new GroupSummary("g-concurrent", "Retried Group", "https://example.com/pic-retried", pictureBytes, "image/png");

        await store.UpsertGroupAsync("g-concurrent", summary, CancellationToken.None);

        var group = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g-concurrent");
        Assert.NotNull(group);
        Assert.Equal("Retried Group", group.GroupName);
        Assert.Equal("https://example.com/pic-retried", group.PictureUrl);
        Assert.Equal("image/png", group.PictureContentType);
        Assert.NotNull(group.Picture);
        Assert.Equal(pictureBytes, group.Picture.Content);
    }

    [Fact]
    public async Task UpsertMemberAsync_WithPictureBytes_WritesContentAndMetadata()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var pictureBytes = new byte[] { 1, 2, 3 };
        var profile = new MemberProfile("u1", "User 1", "https://example.com/pic", pictureBytes, "image/jpeg");

        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        var member = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(member);
        Assert.Equal(pictureBytes, member.Picture?.Content);
        Assert.Equal("image/jpeg", member.PictureContentType);
        Assert.Equal("https://example.com/pic", member.PictureFetchedUrl);
        Assert.NotNull(member.PictureUpdatedAt);
    }

    [Fact]
    public async Task UpsertMemberAsync_WithoutPictureBytes_DoesNotClearExistingPicture()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var pictureBytes = new byte[] { 1, 2, 3 };
        var profile1 = new MemberProfile("u1", "User 1", "https://example.com/pic", pictureBytes, "image/jpeg");
        await store.UpsertMemberAsync("g1", "u1", profile1, CancellationToken.None);

        var profile2 = new MemberProfile("u1", "User 1", "https://example.com/pic2", null, null);
        await store.UpsertMemberAsync("g1", "u1", profile2, CancellationToken.None);

        var member = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(member);
        Assert.Equal(pictureBytes, member.Picture?.Content);
        Assert.Equal("image/jpeg", member.PictureContentType);
        Assert.Equal("https://example.com/pic", member.PictureFetchedUrl);
    }

    [Fact]
    public async Task UpsertMemberAsync_ExistingMemberWithPicture_WithoutPictureBytes_PreservesPictureAndMetadataAndDoesNotTrackBlob()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var originalTime = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "Original Name",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = originalTime,
            PictureContentType = "image/jpeg",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureUpdatedAt = originalTime,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [10, 20, 30] }
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var profile = new MemberProfile("u1", "Updated Name", "https://example.com/pic2", null, null);
        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        // 驗證 ChangeTracker 完全沒有載入或追蹤 GroupMemberPicture
        Assert.Empty(_dbContext.ChangeTracker.Entries<GroupMemberPicture>());

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(reloaded);
        Assert.Equal("Updated Name", reloaded.DisplayName);
        Assert.Equal("https://example.com/pic2", reloaded.PictureUrl);
        Assert.Equal("image/jpeg", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic1", reloaded.PictureFetchedUrl);
        Assert.Equal(originalTime, reloaded.PictureUpdatedAt);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(new byte[] { 10, 20, 30 }, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupMemberPictures.CountAsync(p => p.GroupId == "g1" && p.UserId == "u1"));
    }

    [Fact]
    public async Task UpsertMemberAsync_ExistingMemberWithPicture_WithNewPictureBytes_UpdatesPictureWithoutDuplicates()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            PictureContentType = "image/jpeg",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureUpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [1, 2, 3] }
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var newBytes = new byte[] { 40, 50, 60 };
        var profile = new MemberProfile("u1", "User 1", "https://example.com/pic2", newBytes, "image/png");
        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(reloaded);
        Assert.Equal("image/png", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic2", reloaded.PictureFetchedUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(newBytes, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupMemberPictures.CountAsync(p => p.GroupId == "g1" && p.UserId == "u1"));
    }

    [Fact]
    public async Task UpsertMemberAsync_ExistingMemberWithoutPicture_WithPictureBytes_InsertsPicture()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var pictureBytes = new byte[] { 70, 80, 90 };
        var profile = new MemberProfile("u1", "User 1", "https://example.com/pic1", pictureBytes, "image/webp");
        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(reloaded);
        Assert.Equal("image/webp", reloaded.PictureContentType);
        Assert.Equal("https://example.com/pic1", reloaded.PictureFetchedUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(pictureBytes, reloaded.Picture.Content);
        Assert.Equal(1, await _dbContext.GroupMemberPictures.CountAsync(p => p.GroupId == "g1" && p.UserId == "u1"));
    }

    [Fact]
    public async Task UpsertMemberAsync_ConcurrentInsert_RetriesAndUpdatesAsExpected()
    {
        var interceptor = new SaveFailureInterceptor();
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        var cipher = CreateCipher(false);
        using var dbContext = new SqliteMessageDbContext(options, cipher);
        var store = new DbProfileStore(dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        interceptor.BeforeSaveOnce = async () =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO GroupMembers (GroupId, UserId, DisplayName, UpdatedAt) VALUES ('g-concurrent', 'u-concurrent', 'Concurrent User', '2026-08-16T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        };

        var pictureBytes = new byte[] { 111, 112, 113 };
        var profile = new MemberProfile("u-concurrent", "Retried User", "https://example.com/pic-member-retried", pictureBytes, "image/png");

        await store.UpsertMemberAsync("g-concurrent", "u-concurrent", profile, CancellationToken.None);

        var member = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g-concurrent" && m.UserId == "u-concurrent");
        Assert.NotNull(member);
        Assert.Equal("Retried User", member.DisplayName);
        Assert.Equal("https://example.com/pic-member-retried", member.PictureUrl);
        Assert.Equal("image/png", member.PictureContentType);
        Assert.NotNull(member.Picture);
        Assert.Equal(pictureBytes, member.Picture.Content);
    }

    [Fact]
    public async Task UpsertGroupAsync_EncryptionEnabled_WritesEncryptedContent()
    {
        var cipher = CreateCipher(true);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var pictureBytes = new byte[] { 1, 2, 3 };
        var summary = new GroupSummary("g1", "Group 1", "https://example.com/pic", pictureBytes, "image/jpeg");

        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        var group = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(group);
        Assert.NotNull(group.Picture?.Content);
        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(group.Picture.Content));
    }

    [Fact]
    public async Task GetStalenessAsync_DoesNotLoadPicturesIntoChangeTracker()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureContentType = "image/jpeg",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3] }
        };
        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic2",
            PictureFetchedUrl = "https://example.com/pic2",
            PictureContentType = "image/jpeg",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [4, 5, 6] }
        };
        _dbContext.Groups.Add(group);
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
        Assert.Equal("https://example.com/pic1", staleness.GroupPictureFetchedUrl);
        Assert.Equal("https://example.com/pic2", staleness.MemberPictureFetchedUrl);
        Assert.True(staleness.HasGroupPicture);
        Assert.True(staleness.HasMemberPicture);

        Assert.Empty(_dbContext.ChangeTracker.Entries<GroupPicture>());
        Assert.Empty(_dbContext.ChangeTracker.Entries<GroupMemberPicture>());
        Assert.Empty(_dbContext.ChangeTracker.Entries<Group>());
        Assert.Empty(_dbContext.ChangeTracker.Entries<GroupMember>());
    }
}
