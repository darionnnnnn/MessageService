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

    [Fact]
    public async Task GetStalenessAsync_Group_FreshUpdatedAt_WithPictureUrl_NoPictureInChildTable_IsStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.True(staleness.GroupStale);
        Assert.False(staleness.HasGroupPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_Group_FreshUpdatedAt_NullPictureUrl_NoPictureInChildTable_IsNotStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.False(staleness.HasGroupPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_Group_FreshUpdatedAt_WithPictureUrl_WithPictureInChildTable_IsNotStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            PictureFetchedUrl = "https://example.com/pic1",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3] }
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.True(staleness.HasGroupPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_Member_FreshUpdatedAt_WithPictureUrl_NoPictureInChildTable_IsStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic2",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.True(staleness.MemberStale);
        Assert.False(staleness.HasMemberPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_Member_FreshUpdatedAt_NullPictureUrl_NoPictureInChildTable_IsNotStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.MemberStale);
        Assert.False(staleness.HasMemberPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_Member_FreshUpdatedAt_WithPictureUrl_WithPictureInChildTable_IsNotStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic2",
            PictureFetchedUrl = "https://example.com/pic2",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [4, 5, 6] }
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.MemberStale);
        Assert.True(staleness.HasMemberPicture);
    }

    [Fact]
    public async Task GetStalenessAsync_GroupAndMember_WhitespacePictureUrl_NoPictureInChildTable_IsNotStale()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "   ",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "   ",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.Groups.Add(group);
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task GetStalenessAsync_Group_PictureUrlAlreadyTriedAndUnavailable_IsNotStale()
    {
        // PictureFetchedUrl 等於目前的 PictureUrl 代表「這個網址試過而且永久拿不到」
        // （檔案超過上限、404）——再判為過期就會變成無限期的每 10 分鐘重抓
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/too-big.png",
            PictureFetchedUrl = "https://example.com/too-big.png",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
    }

    [Fact]
    public async Task GetStalenessAsync_Group_PictureUrlChangedAfterPermanentFailure_IsStaleAgain()
    {
        // 換了一張新頭貼就要重新試——「試過了」只對當時那個網址成立
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/new.png",
            PictureFetchedUrl = "https://example.com/old-too-big.png",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.True(staleness.GroupStale);
    }

    [Fact]
    public async Task UpsertGroupAsync_PicturePermanentlyUnavailable_StampsFetchedUrlSoItStopsRetrying()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        await store.UpsertGroupAsync(
            "g1",
            new GroupSummary("g1", "Group 1", "https://example.com/too-big.png", PicturePermanentlyUnavailable: true),
            CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var group = await _dbContext.Groups.FindAsync("g1");
        Assert.Equal("https://example.com/too-big.png", group!.PictureFetchedUrl);

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
        Assert.False(staleness.GroupStale);
    }

    [Fact]
    public async Task UpsertMemberAsync_PicturePermanentlyUnavailable_StampsFetchedUrlSoItStopsRetrying()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        await store.UpsertMemberAsync(
            "g1",
            "u1",
            new MemberProfile("u1", "Member 1", "https://example.com/too-big.png", PicturePermanentlyUnavailable: true),
            CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task UpsertGroupAsync_TransientPictureFailure_LeavesFetchedUrlAloneSoItRetries()
    {
        // 暫時性失敗（防火牆不通）不能蓋掉 PictureFetchedUrl，否則修好後就不會自動補圖
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        await store.UpsertGroupAsync(
            "g1",
            new GroupSummary("g1", "Group 1", "https://example.com/pic.png", PictureDownloadFailed: true),
            CancellationToken.None);
        _dbContext.ChangeTracker.Clear();

        var group = await _dbContext.Groups.FindAsync("g1");
        Assert.Null(group!.PictureFetchedUrl);

        var staleness = await store.GetStalenessAsync("g1", null, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
        Assert.True(staleness.GroupStale);
    }

    [Fact]
    public async Task GetStalenessAsync_PictureUrlChanged_WithExistingPicture_IsStale()
    {
        // 核心修正：成員換頭貼後 PictureUrl 為新網址，但若先前下載失敗時名稱仍會寫入、UpdatedAt 落在有效期間內，
        // 且既有舊圖仍在。缺圖子句不應被 hasPicture 短路，只要 PictureUrl 與 PictureFetchedUrl 不同即判為過期。
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/new-pic.png",
            PictureFetchedUrl = "https://example.com/old-pic.png",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3] }
        });
        _dbContext.GroupMembers.Add(new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/new-member-pic.png",
            PictureFetchedUrl = "https://example.com/old-member-pic.png",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [4, 5, 6] }
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.True(staleness.GroupStale);
        Assert.True(staleness.MemberStale);
    }

    [Fact]
    public async Task GetStalenessAsync_PictureUrlEqualsPictureFetchedUrl_IsNotStale()
    {
        // 永久失敗閂鎖：PictureFetchedUrl 等於 PictureUrl 代表該網址已記錄嘗試過（例如 404 或超過大小），未過期時不應再判為過期
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1.png",
            PictureFetchedUrl = "https://example.com/pic1.png",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _dbContext.GroupMembers.Add(new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic2.png",
            PictureFetchedUrl = "https://example.com/pic2.png",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task GetStalenessAsync_FreshUpdatedAt_SamePictureUrl_WithPicture_IsNotStale()
    {
        // UpdatedAt 未到期、網址相同且已有圖 → 不過期
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/group.png",
            PictureFetchedUrl = "https://example.com/group.png",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2] }
        });
        _dbContext.GroupMembers.Add(new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/user.png",
            PictureFetchedUrl = "https://example.com/user.png",
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [3, 4] }
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var staleness = await store.GetStalenessAsync("g1", "u1", DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);

        Assert.False(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task UpsertGroupAsync_WithNullPictureUrl_DeletesChildPictureAndClearsMetadata()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        var group = new Group
        {
            GroupId = "g1",
            GroupName = "Group 1",
            PictureUrl = "https://example.com/pic1",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureContentType = "image/jpeg",
            PictureUpdatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupPicture { GroupId = "g1", Content = [1, 2, 3] }
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var summary = new GroupSummary("g1", "Group 1", null, null, null);
        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.Picture);
        Assert.Null(reloaded.PictureUrl);
        Assert.Null(reloaded.PictureContentType);
        Assert.Null(reloaded.PictureFetchedUrl);
        Assert.Null(reloaded.PictureUpdatedAt);
        Assert.Equal(0, await _dbContext.GroupPictures.CountAsync(p => p.GroupId == "g1"));
    }

    [Fact]
    public async Task UpsertMemberAsync_WithNullPictureUrl_DeletesChildPictureAndClearsMetadata()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "User 1",
            PictureUrl = "https://example.com/pic1",
            PictureFetchedUrl = "https://example.com/pic1",
            PictureContentType = "image/jpeg",
            PictureUpdatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [1, 2, 3] }
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var profile = new MemberProfile("u1", "User 1", null, null, null);
        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.Picture);
        Assert.Null(reloaded.PictureUrl);
        Assert.Null(reloaded.PictureContentType);
        Assert.Null(reloaded.PictureFetchedUrl);
        Assert.Null(reloaded.PictureUpdatedAt);
        Assert.Equal(0, await _dbContext.GroupMemberPictures.CountAsync(p => p.GroupId == "g1" && p.UserId == "u1"));
    }

    [Fact]
    public async Task UpsertGroupAsync_WithPictureUrlAndNullPictureBytes_PreservesPictureRowAndUpdatesName()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        var originalTime = DateTimeOffset.UtcNow.AddHours(-2);
        var group = new Group
        {
            GroupId = "g1",
            GroupName = "舊群組名稱",
            PictureUrl = "https://example.com/old-pic.png",
            PictureFetchedUrl = "https://example.com/old-pic.png",
            PictureContentType = "image/png",
            PictureUpdatedAt = originalTime,
            UpdatedAt = originalTime,
            Picture = new GroupPicture { GroupId = "g1", Content = [10, 20, 30] }
        };
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var summary = new GroupSummary("g1", "新群組名稱", "https://example.com/new-pic.png", null, null, PictureDownloadFailed: true);
        await store.UpsertGroupAsync("g1", summary, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == "g1");
        Assert.NotNull(reloaded);
        Assert.Equal("新群組名稱", reloaded.GroupName);
        Assert.Equal("https://example.com/new-pic.png", reloaded.PictureUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(new byte[] { 10, 20, 30 }, reloaded.Picture.Content);
        Assert.Equal("image/png", reloaded.PictureContentType);
        Assert.Equal("https://example.com/old-pic.png", reloaded.PictureFetchedUrl);
    }

    [Fact]
    public async Task UpsertMemberAsync_WithPictureUrlAndNullPictureBytes_PreservesPictureRowAndUpdatesName()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        var originalTime = DateTimeOffset.UtcNow.AddHours(-2);
        var member = new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "舊成員名稱",
            PictureUrl = "https://example.com/old-member.png",
            PictureFetchedUrl = "https://example.com/old-member.png",
            PictureContentType = "image/png",
            PictureUpdatedAt = originalTime,
            UpdatedAt = originalTime,
            Picture = new GroupMemberPicture { GroupId = "g1", UserId = "u1", Content = [40, 50, 60] }
        };
        _dbContext.GroupMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var profile = new MemberProfile("u1", "新成員名稱", "https://example.com/new-member.png", null, null, PictureDownloadFailed: true);
        await store.UpsertMemberAsync("g1", "u1", profile, CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var reloaded = await _dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == "g1" && m.UserId == "u1");
        Assert.NotNull(reloaded);
        Assert.Equal("新成員名稱", reloaded.DisplayName);
        Assert.Equal("https://example.com/new-member.png", reloaded.PictureUrl);
        Assert.NotNull(reloaded.Picture);
        Assert.Equal(new byte[] { 40, 50, 60 }, reloaded.Picture.Content);
        Assert.Equal("image/png", reloaded.PictureContentType);
        Assert.Equal("https://example.com/old-member.png", reloaded.PictureFetchedUrl);
    }

    [Fact]
    public async Task UpsertGroupAsync_NameProtection_PreservesExistingNameWhenNewNameIsNullOrWhitespace()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.Groups.Add(new Group
        {
            GroupId = "g1",
            GroupName = "甲",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // 帶 null -> 仍是「甲」
        await store.UpsertGroupAsync("g1", new GroupSummary("g1", null, null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded1 = await _dbContext.Groups.FindAsync("g1");
        Assert.Equal("甲", reloaded1!.GroupName);

        // 帶 "" -> 仍是「甲」
        await store.UpsertGroupAsync("g1", new GroupSummary("g1", "", null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded2 = await _dbContext.Groups.FindAsync("g1");
        Assert.Equal("甲", reloaded2!.GroupName);

        // 帶 "乙" -> 變「乙」
        await store.UpsertGroupAsync("g1", new GroupSummary("g1", "乙", null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded3 = await _dbContext.Groups.FindAsync("g1");
        Assert.Equal("乙", reloaded3!.GroupName);
    }

    [Fact]
    public async Task UpsertMemberAsync_NameProtection_PreservesExistingDisplayNameWhenNewDisplayNameIsNullOrWhitespace()
    {
        var store = new DbProfileStore(_dbContext, CreateCipher(false), NullLogger<DbProfileStore>.Instance);

        _dbContext.GroupMembers.Add(new GroupMember
        {
            GroupId = "g1",
            UserId = "u1",
            DisplayName = "甲",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // 帶 null -> 仍是「甲」
        await store.UpsertMemberAsync("g1", "u1", new MemberProfile("u1", null, null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded1 = await _dbContext.GroupMembers.FindAsync("g1", "u1");
        Assert.Equal("甲", reloaded1!.DisplayName);

        // 帶 "" -> 仍是「甲」
        await store.UpsertMemberAsync("g1", "u1", new MemberProfile("u1", "", null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded2 = await _dbContext.GroupMembers.FindAsync("g1", "u1");
        Assert.Equal("甲", reloaded2!.DisplayName);

        // 帶 "乙" -> 變「乙」
        await store.UpsertMemberAsync("g1", "u1", new MemberProfile("u1", "乙", null), CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        var reloaded3 = await _dbContext.GroupMembers.FindAsync("g1", "u1");
        Assert.Equal("乙", reloaded3!.DisplayName);
    }

    [Fact]
    public async Task GetStaleProfilesAsync_ReturnsStaleGroupAndMissingPictureMember_IgnoresFresh()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-7);

        // 1. UpdatedAt 過期的群組
        _dbContext.Groups.Add(new Group
        {
            GroupId = "g_stale",
            GroupName = "過期群組",
            UpdatedAt = now.AddDays(-8)
        });

        // 2. 缺圖的成員（有 PictureUrl 但沒有圖片子列且 PictureFetchedUrl != PictureUrl）
        _dbContext.GroupMembers.Add(new GroupMember
        {
            GroupId = "g_member_missing_pic",
            UserId = "u_missing_pic",
            DisplayName = "缺圖成員",
            PictureUrl = "https://example.com/pic.png",
            PictureFetchedUrl = null,
            UpdatedAt = now
        });

        // 3. 完全正常的群組（未過期、有圖且 PictureFetchedUrl == PictureUrl）
        _dbContext.Groups.Add(new Group
        {
            GroupId = "g_normal",
            GroupName = "正常群組",
            PictureUrl = "https://example.com/normal.png",
            PictureFetchedUrl = "https://example.com/normal.png",
            UpdatedAt = now,
            Picture = new GroupPicture { GroupId = "g_normal", Content = [1, 2] }
        });

        // 4. 永久失敗的群組（未過期、PictureFetchedUrl == PictureUrl 且無圖）
        _dbContext.Groups.Add(new Group
        {
            GroupId = "g_perm_failed",
            GroupName = "永久失敗群組",
            PictureUrl = "https://example.com/perm.png",
            PictureFetchedUrl = "https://example.com/perm.png",
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var results = await store.GetStaleProfilesAsync(100, cutoff, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, t => t.GroupId == "g_stale" && t.UserId == null);
        Assert.Contains(results, t => t.GroupId == "g_member_missing_pic" && t.UserId == "u_missing_pic");
        Assert.DoesNotContain(results, t => t.GroupId == "g_normal");
        Assert.DoesNotContain(results, t => t.GroupId == "g_perm_failed");
    }

    [Fact]
    public async Task GetStaleProfilesAsync_CandidatesExceedLimit_ReturnsExactLimitWithGroupsFirst()
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-7);

        for (var i = 0; i < 5; i++)
        {
            _dbContext.Groups.Add(new Group
            {
                GroupId = $"g_{i}",
                UpdatedAt = now.AddDays(-10 - i)
            });
        }

        for (var i = 0; i < 5; i++)
        {
            _dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = $"g_m_{i}",
                UserId = $"u_m_{i}",
                UpdatedAt = now.AddDays(-10 - i)
            });
        }

        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var limit3 = await store.GetStaleProfilesAsync(3, cutoff, CancellationToken.None);
        Assert.Equal(3, limit3.Count);
        Assert.All(limit3, t => Assert.Null(t.UserId)); // 群組優先

        var limit7 = await store.GetStaleProfilesAsync(7, cutoff, CancellationToken.None);
        Assert.Equal(7, limit7.Count);
        Assert.Equal(5, limit7.Count(t => t.UserId == null)); // 5 個群組
        Assert.Equal(2, limit7.Count(t => t.UserId != null)); // 剩餘配額給 2 個成員
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetStaleProfilesAsync_MaxZeroOrNegative_ReturnsEmpty(int max)
    {
        var cipher = CreateCipher(false);
        var store = new DbProfileStore(_dbContext, cipher, NullLogger<DbProfileStore>.Instance);

        var results = await store.GetStaleProfilesAsync(max, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(results);
    }
}
