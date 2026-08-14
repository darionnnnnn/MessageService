using System.Net;
using System.Net.Http.Headers;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class LineProfileClientTests
{
    private static IOptions<LineOptions> CreateOptions() => 
        Microsoft.Extensions.Options.Options.Create(new LineOptions { ChannelAccessToken = "test-token" });

    [Fact]
    public async Task GetGroupSummaryAsync_DownloadsPicture_WhenValidPictureUrl()
    {
        var pictureBytes = new byte[] { 1, 2, 3 };
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/v2/bot/group/123/summary"))
            {
                var content = new StringContent("{\"groupId\":\"123\", \"groupName\":\"Test Group\", \"pictureUrl\":\"https://example.com/pic.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.RequestUri.ToString() == "https://example.com/pic.jpg")
            {
                var content = new ByteArrayContent(pictureBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var result = await client.GetGroupSummaryAsync("123", null, false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("123", result.GroupId);
        Assert.Equal("Test Group", result.GroupName);
        Assert.Equal("https://example.com/pic.jpg", result.PictureUrl);
        Assert.Equal(pictureBytes, result.PictureBytes);
        Assert.Equal("image/jpeg", result.PictureContentType);
    }

    [Fact]
    public async Task GetGroupSummaryAsync_PictureDownloadFails_ReturnsProfileWithoutPicture()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/v2/bot/group/123/summary"))
            {
                var content = new StringContent("{\"groupId\":\"123\", \"groupName\":\"Test Group\", \"pictureUrl\":\"https://example.com/pic.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.RequestUri.ToString() == "https://example.com/pic.jpg")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var result = await client.GetGroupSummaryAsync("123", null, false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("123", result.GroupId);
        Assert.Equal("Test Group", result.GroupName);
        Assert.Null(result.PictureBytes);
        Assert.Null(result.PictureContentType);
        Assert.Equal("https://example.com/pic.jpg", result.PictureUrl);
    }

    [Fact]
    public async Task GetGroupSummaryAsync_SamePictureUrl_SkipsDownload()
    {
        var imageRequested = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/v2/bot/group/123/summary"))
            {
                var content = new StringContent("{\"groupId\":\"123\", \"groupName\":\"Test Group\", \"pictureUrl\":\"https://example.com/pic.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.RequestUri.ToString() == "https://example.com/pic.jpg")
            {
                imageRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[0]) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var result = await client.GetGroupSummaryAsync("123", "https://example.com/pic.jpg", true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(imageRequested);
        Assert.Null(result.PictureBytes);
    }
}
