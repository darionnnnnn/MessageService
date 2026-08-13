using System.Net;
using System.Net.Http.Json;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class HttpIngestSinkTests
{
    private static IngestEnvelope SampleEnvelope(string webhookEventId = "evt-1") => new(
        WebhookEventId: webhookEventId,
        LineMessageId: "m1",
        GroupId: "G1",
        UserId: "U1",
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.UtcNow,
        ReceivedAt: DateTimeOffset.UtcNow,
        HasContent: false,
        ContentFileName: null);

    private static (HttpIngestSink sink, FakeHttpMessageHandler handler) CreateSink(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string apiKey = "test-key")
    {
        var handler = new FakeHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://db-host.example/") };
        var sink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = apiKey }));
        return (sink, handler);
    }

    private static HttpResponseMessage OkWithBody(long? contentId = null) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new IngestEventResponse(contentId))
    };

    [Fact]
    public async Task SubmitAsync_2xxResponse_ReturnsNormally()
    {
        var (sink, _) = CreateSink(_ => OkWithBody());

        var ex = await Record.ExceptionAsync(() => sink.SubmitAsync(SampleEnvelope(), CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SubmitAsync_2xxResponse_ReturnsContentIdFromBody()
    {
        var (sink, _) = CreateSink(_ => OkWithBody(contentId: 42));

        var result = await sink.SubmitAsync(SampleEnvelope(), CancellationToken.None);

        Assert.Equal(42, result.ContentId);
    }

    [Fact]
    public async Task SubmitAsync_2xxResponse_NoContentId_ReturnsNullContentId()
    {
        var (sink, _) = CreateSink(_ => OkWithBody());

        var result = await sink.SubmitAsync(SampleEnvelope(), CancellationToken.None);

        Assert.Null(result.ContentId);
    }

    [Fact]
    public async Task SubmitAsync_SendsCorrectPathAndApiKeyHeader()
    {
        var (sink, handler) = CreateSink(_ => OkWithBody(), apiKey: "the-shared-secret");

        await sink.SubmitAsync(SampleEnvelope(), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://db-host.example/api/ingest/events", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("the-shared-secret", handler.LastRequest.Headers.GetValues("X-Ingest-Key").Single());
    }

    [Fact]
    public async Task SubmitAsync_400Response_ThrowsPermanentIngestException()
    {
        // 400＝payload 格式不合，重試不會變好——這裡故意跟其他 4xx／5xx 分開處理，
        // 讓 forwarder 直接死信而不是浪費重試次數
        var (sink, _) = CreateSink(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid envelope shape")
        });

        var ex = await Assert.ThrowsAsync<PermanentIngestException>(
            () => sink.SubmitAsync(SampleEnvelope(), CancellationToken.None));
        Assert.Contains("invalid envelope shape", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)] // 金鑰設錯——修好設定後重試會成功，不是永久失敗
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SubmitAsync_OtherErrorStatusCodes_ThrowsRetryableException_NotPermanent(HttpStatusCode statusCode)
    {
        var (sink, _) = CreateSink(_ => new HttpResponseMessage(statusCode));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.SubmitAsync(SampleEnvelope(), CancellationToken.None));
        Assert.IsNotType<PermanentIngestException>(ex);
    }

    [Fact]
    public async Task SubmitAsync_NetworkFailure_ThrowsRetryableException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://db-host.example/") };
        var sink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = "key" }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.SubmitAsync(SampleEnvelope(), CancellationToken.None));
        Assert.IsNotType<PermanentIngestException>(ex);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }
}
