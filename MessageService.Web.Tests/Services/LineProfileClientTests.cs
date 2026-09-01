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
    private static IOptionsMonitor<LineOptions> CreateOptions(LineOptions? options = null) => 
        new FakeOptionsMonitor<LineOptions>(options ?? new LineOptions { ChannelAccessToken = "test-token" });

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
        Assert.True(result.PictureDownloadFailed);
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
        Assert.False(result.PictureDownloadFailed);
    }

    [Fact]
    public async Task GetGroupMemberProfileAsync_DownloadsPicture_WhenValidPictureUrl()
    {
        var pictureBytes = new byte[] { 4, 5, 6 };
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/v2/bot/group/123/member/u123"))
            {
                var content = new StringContent("{\"userId\":\"u123\", \"displayName\":\"Test User\", \"pictureUrl\":\"https://example.com/member.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.RequestUri.ToString() == "https://example.com/member.jpg")
            {
                var content = new ByteArrayContent(pictureBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var result = await client.GetGroupMemberProfileAsync("123", "u123", null, false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("u123", result.UserId);
        Assert.Equal("Test User", result.DisplayName);
        Assert.Equal("https://example.com/member.jpg", result.PictureUrl);
        Assert.Equal(pictureBytes, result.PictureBytes);
        Assert.Equal("image/jpeg", result.PictureContentType);
        Assert.False(result.PictureDownloadFailed);
    }

    [Fact]
    public async Task GetGroupMemberProfileAsync_PictureDownloadFails_ReturnsProfileWithPictureDownloadFailedTrue()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/v2/bot/group/123/member/u123"))
            {
                var content = new StringContent("{\"userId\":\"u123\", \"displayName\":\"Test User\", \"pictureUrl\":\"https://example.com/member.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.RequestUri.ToString() == "https://example.com/member.jpg")
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var result = await client.GetGroupMemberProfileAsync("123", "u123", null, false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("u123", result.UserId);
        Assert.Equal("Test User", result.DisplayName);
        Assert.Null(result.PictureBytes);
        Assert.Null(result.PictureContentType);
        Assert.Equal("https://example.com/member.jpg", result.PictureUrl);
        Assert.True(result.PictureDownloadFailed);
    }

    [Fact]
    public async Task GetGroupSummaryAsync_PictureTooLarge_ReportsPermanentlyUnavailable()
    {
        // 過大的圖重試多少次都一樣，必須回報成「永久不可得」——回報成暫時失敗的話，
        // staleness 的缺圖條件會讓同一張圖被無限期地每 10 分鐘重抓
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/v2/bot/group/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"groupId":"G1","groupName":"群組","pictureUrl":"https://profile.line-scdn.net/big.png"}""")
                };
            }

            var oversized = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16])
            };
            oversized.Content.Headers.ContentLength = LineProfileClient.MaxImageSize + 1;
            return oversized;
        });

        var client = new LineProfileClient(
            new FakeHttpClientFactory(handler), CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var summary = await client.GetGroupSummaryAsync("G1", null, false, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Null(summary!.PictureBytes);
        Assert.True(summary.PicturePermanentlyUnavailable);
        Assert.False(summary.PictureDownloadFailed);
    }

    [Fact]
    public async Task GetGroupSummaryAsync_PictureNotFound_ReportsPermanentlyUnavailable()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("/v2/bot/group/")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"groupId":"G1","groupName":"群組","pictureUrl":"https://profile.line-scdn.net/gone.png"}""")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var client = new LineProfileClient(
            new FakeHttpClientFactory(handler), CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var summary = await client.GetGroupSummaryAsync("G1", null, false, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.True(summary!.PicturePermanentlyUnavailable);
        Assert.False(summary.PictureDownloadFailed);
    }

    [Fact]
    public async Task GetGroupSummaryAsync_PictureConnectionFails_ReportsTransientFailure()
    {
        // 連不上是暫時性的（防火牆修好就會成功），必須維持重試路徑
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/v2/bot/group/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"groupId":"G1","groupName":"群組","pictureUrl":"https://profile.line-scdn.net/pic.png"}""")
                };
            }

            throw new HttpRequestException("connection refused",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        });

        var client = new LineProfileClient(
            new FakeHttpClientFactory(handler), CreateOptions(), NullLogger<LineProfileClient>.Instance);

        var summary = await client.GetGroupSummaryAsync("G1", null, false, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.True(summary!.PictureDownloadFailed);
        Assert.False(summary.PicturePermanentlyUnavailable);
    }

    [Fact]
    public async Task DownloadPictureAsync_HotReloads_WhenOptionsChange()
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        });

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            if (req.RequestUri.AbsolutePath.Contains("/v2/bot/group/123/summary"))
            {
                var content = new StringContent("{\"groupId\":\"123\", \"groupName\":\"Test Group\", \"pictureUrl\":\"https://profile.line-scdn.net/pic.jpg\"}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });

        var factory = new FakeHttpClientFactory(handler);
        var client = new LineProfileClient(factory, monitor, NullLogger<LineProfileClient>.Instance);

        // 1. Direct -> requests original picture URL directly
        await client.GetGroupSummaryAsync("123", null, false, CancellationToken.None);
        Assert.Contains("https://profile.line-scdn.net/pic.jpg", requestedUrls);

        // 2. Change to EdgeProxy
        requestedUrls.Clear();
        monitor.CurrentValue = new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        };

        await client.GetGroupSummaryAsync("123", null, false, CancellationToken.None);
        Assert.Contains("https://proxy.example/MSLine/line/image/profile.line-scdn.net/pic.jpg", requestedUrls);

        // 3. Change back to Direct
        requestedUrls.Clear();
        monitor.CurrentValue = new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        };

        await client.GetGroupSummaryAsync("123", null, false, CancellationToken.None);
        Assert.Contains("https://profile.line-scdn.net/pic.jpg", requestedUrls);
    }
}
