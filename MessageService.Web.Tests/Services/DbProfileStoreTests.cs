using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using MessageService.Options;
using MessageService.Models;

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
}
