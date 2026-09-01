using System.Net;
using System.Net.Http.Headers;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessageService.Tests.Services;

// GetContentAsync 改成串流回傳（不再整份 ReadAsByteArrayAsync）——這組測試釘住串流內容讀得到、
// ContentType/ContentLength 從回應標頭正確帶出、失敗時往外拋例外三件事
public class LineContentClientTests
{
    private static LineContentClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder, out FakeHttpMessageHandler handler)
    {
        handler = new FakeHttpMessageHandler(responder);
        var authHandler = new MessageService.Web.Services.LineAuthorizationHandler(
            new FakeOptionsMonitor<LineOptions>(new LineOptions { ChannelAccessToken = "test-token" }),
            NullLogger<MessageService.Web.Services.LineAuthorizationHandler>.Instance,
            TimeProvider.System)
        {
            InnerHandler = handler
        };
        var factory = new FakeHttpClientFactory(authHandler);
        return new LineContentClient(factory);
    }

    private static IOptions<LineOptions> OptionsFactoryCreate(LineOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);

    [Fact]
    public async Task GetContentAsync_ReadsFullStreamContent()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5]) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }
        }, out _);

        await using var result = await client.GetContentAsync("msg-1", CancellationToken.None);

        using var buffer = new MemoryStream();
        await result.Content.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer.ToArray());
    }

    [Fact]
    public async Task GetContentAsync_ExposesContentTypeAndLength_FromResponseHeaders()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") } }
        }, out _);

        await using var result = await client.GetContentAsync("msg-1", CancellationToken.None);

        Assert.Equal("video/mp4", result.ContentType);
        Assert.Equal(3, result.ContentLength);
    }

    [Fact]
    public async Task GetContentAsync_RequestsCorrectPath_WithBearerToken()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }, out var handler);

        await using var result = await client.GetContentAsync("msg-42", CancellationToken.None);

        // FakeHttpClientFactory 固定發回帶 BaseAddress 的 client（比照 ApiContentWorkSourceTests 的手法）；
        // LineContentClient 對「api-data.line.me」的預設值只在 IHttpClientFactory 沒設 BaseAddress 時才生效
        Assert.Equal("https://db-host.example/v2/bot/message/msg-42/content", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetContentAsync_NonSuccessStatus_Throws()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound), out _);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetContentAsync("msg-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetTranscodingStatusAsync_ParsesStatusField()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"succeeded"}""")
        }, out _);

        var status = await client.GetTranscodingStatusAsync("msg-1", CancellationToken.None);

        Assert.Equal(TranscodingStatus.Succeeded, status);
    }
}
