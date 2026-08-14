using System.Net;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageService.Web.Tests.Api;

public class AvatarsControllerTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new(encryptionKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetGroupAvatar_WhenPictureExists_ReturnsUnencryptedPicture()
    {
        var pictureBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var now = DateTimeOffset.UtcNow;
        
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "Group 1",
                PictureContent = pictureBytes,
                PictureContentType = "image/jpeg",
                PictureUpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetAsync("/api/groups/G1/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
        
        var cacheControl = response.Headers.CacheControl?.ToString();
        Assert.NotNull(cacheControl);
        Assert.Contains("private", cacheControl);
        Assert.Contains("no-cache", cacheControl);
        Assert.NotNull(response.Headers.ETag);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(pictureBytes, body);
    }

    [Fact]
    public async Task GetGroupAvatar_WhenPictureExists_Encrypted_ReturnsDecryptedPicture()
    {
        var pictureBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var cipher = _fixture.Factory.Services.GetService(typeof(FieldCipher)) as FieldCipher;
        Assert.NotNull(cipher);

        // Encrypt the picture bytes
        using var ms = new MemoryStream();
        using var source = new MemoryStream(pictureBytes);
        using (var encryptingStream = cipher.CreateEncryptingStream(source, pictureBytes.Length))
        {
            await encryptingStream.CopyToAsync(ms);
        }
        var encryptedBytes = ms.ToArray();
        
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "Group 1",
                PictureContent = encryptedBytes,
                PictureContentType = "image/jpeg",
                PictureUpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetAsync("/api/groups/G1/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(pictureBytes, body);
    }

    [Fact]
    public async Task GetGroupAvatar_WhenNoPicture_ReturnsNotFound()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "Group 1",
                PictureContent = null,
                PictureUpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetAsync("/api/groups/G1/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Anonymous")]
    [InlineData("MaskMiddle")]
    [InlineData("CustomAlias")]
    public async Task GetGroupAvatar_WhenNotOriginalDisplayMode_ReturnsNotFound(string mode)
    {
        var pictureBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = Enum.Parse<NameDisplayMode>(mode);
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "Group 1",
                PictureContent = pictureBytes,
                PictureContentType = "image/jpeg",
                PictureUpdatedAt = now
            });
        });

        var response = await _fixture.Client.GetAsync("/api/groups/G1/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGroupAvatar_WithMatchingETag_ReturnsNotModified()
    {
        var pictureBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "Group 1",
                PictureContent = pictureBytes,
                PictureContentType = "image/jpeg",
                PictureUpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response1 = await _fixture.Client.GetAsync("/api/groups/G1/avatar");
        var etag = response1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "/api/groups/G1/avatar");
        request2.Headers.IfNoneMatch.ParseAdd(etag);
        var response2 = await _fixture.Client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.NotModified, response2.StatusCode);
    }

    [Fact]
    public async Task GetMemberAvatar_WhenPictureExists_ReturnsPicture()
    {
        var pictureBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U1",
                DisplayName = "User 1",
                PictureContent = pictureBytes,
                PictureContentType = "image/png",
                PictureUpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetAsync("/api/groups/G1/members/U1/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(pictureBytes, body);
    }
}
