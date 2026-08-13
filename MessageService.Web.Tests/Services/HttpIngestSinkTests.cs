using System.Net;
using System.Net.Http.Json;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
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
        var sink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = apiKey }), NullLogger<HttpIngestSink>.Instance);
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
        var sink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = "key" }), NullLogger<HttpIngestSink>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.SubmitAsync(SampleEnvelope(), CancellationToken.None));
        Assert.IsNotType<PermanentIngestException>(ex);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // === SubmitBatchAsync（問題9） ===

    private static HttpResponseMessage BatchOkWithBody(params IngestBatchItemResult[] results) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(results.ToList())
    };

    [Fact]
    public async Task SubmitBatchAsync_EmptyList_ReturnsEmptyWithoutSendingRequest()
    {
        var (sink, handler) = CreateSink(_ => throw new InvalidOperationException("should not be called"));

        var results = await sink.SubmitBatchAsync([], CancellationToken.None);

        Assert.Empty(results);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SubmitBatchAsync_SendsCorrectPathAndApiKeyHeaderAndWholeListAsBody()
    {
        var (sink, handler) = CreateSink(
            _ => BatchOkWithBody(new IngestBatchItemResult("evt-1", null, false, null)),
            apiKey: "the-shared-secret");

        await sink.SubmitBatchAsync([SampleEnvelope("evt-1")], CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://db-host.example/api/ingest/events-batch", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("the-shared-secret", handler.LastRequest.Headers.GetValues("X-Ingest-Key").Single());
    }

    [Fact]
    public async Task SubmitBatchAsync_2xxResponse_ReturnsResultsFromBody()
    {
        var (sink, _) = CreateSink(_ => BatchOkWithBody(
            new IngestBatchItemResult("evt-1", 42, false, null),
            new IngestBatchItemResult("evt-2", null, true, "malformed")));

        var results = await sink.SubmitBatchAsync([SampleEnvelope("evt-1"), SampleEnvelope("evt-2")], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(42, results[0].ContentId);
        Assert.False(results[0].PermanentlyRejected);
        Assert.True(results[1].PermanentlyRejected);
        Assert.Equal("malformed", results[1].Error);
    }

    [Fact]
    public async Task SubmitBatchAsync_400Response_FallsBackToOneByOne_IsolatingThePoisonItem()
    {
        // 體檢輪抓到的真 bug 的釘子：整包 400（ASP.NET Core 模型驗證對整個請求回 400，分不出
        // 是哪一筆有問題）原本擲 PermanentIngestException——但 forwarder 的批次層級 catch 不分
        // 型別，會把它當暫時性失敗無限退避重試：毒項目永不死信、還連坐同批健康項目。
        // 正確行為是退回逐筆隔離：毒項目單獨拿到 400 → PermanentlyRejected（forwarder 據此
        // 死信），健康項目照常落地——恢復合併前逐筆版「單筆死信、其餘照走」的語意
        var envelopes = new List<IngestEnvelope> { SampleEnvelope("evt-good"), SampleEnvelope("evt-poison") };
        var (sink, _) = CreateSink(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("events-batch"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid batch shape") };
            }
            var body = request.Content!.ReadAsStringAsync().Result;
            return body.Contains("evt-poison")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad envelope") }
                : OkWithBody();
        });

        var results = await sink.SubmitBatchAsync(envelopes, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results.Single(r => r.WebhookEventId == "evt-good").PermanentlyRejected);
        Assert.True(results.Single(r => r.WebhookEventId == "evt-poison").PermanentlyRejected);
    }

    [Fact]
    public async Task SubmitBatchAsync_2xxWithNullBody_ThrowsRetryableException_NotEmptyList()
    {
        // 200 但空 body（Core 端 bug 才會發生）不能回空清單——空清單對 forwarder 代表
        // 「這批誰都沒被處理到」，項目原樣留著會立刻重跑，變成無退避的熱迴圈打爆 Core
        var (sink, _) = CreateSink(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.SubmitBatchAsync([SampleEnvelope()], CancellationToken.None));
        Assert.IsNotType<PermanentIngestException>(ex);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SubmitBatchAsync_OtherErrorStatusCodes_ThrowsRetryableException_NotPermanent(HttpStatusCode statusCode)
    {
        var (sink, _) = CreateSink(_ => new HttpResponseMessage(statusCode));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.SubmitBatchAsync([SampleEnvelope()], CancellationToken.None));
        Assert.IsNotType<PermanentIngestException>(ex);
    }

    [Fact]
    public async Task SubmitBatchAsync_BatchEndpointReturns404_FallsBackToOneByOne()
    {
        // 相容性：Edge 新版打到還沒升級的 Core（沒有批次端點）——見 docs/DEPLOYMENT-MODES.md
        // 「先升 Core 再升 Edge」。退回逐筆模式打既有的單筆端點，一樣能完成整批。
        var envelopes = new List<IngestEnvelope> { SampleEnvelope("evt-1"), SampleEnvelope("evt-2") };
        var oneByOneCallCount = 0;
        var (sink, handler) = CreateSink(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("events-batch"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            oneByOneCallCount++;
            return OkWithBody(contentId: oneByOneCallCount);
        });

        var results = await sink.SubmitBatchAsync(envelopes, CancellationToken.None);

        Assert.Equal(2, oneByOneCallCount);
        Assert.Equal(["evt-1", "evt-2"], results.Select(r => r.WebhookEventId));
        Assert.All(results, r => Assert.False(r.PermanentlyRejected));
        Assert.NotNull(handler.LastRequest);
        Assert.EndsWith("/api/ingest/events", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SubmitBatchAsync_FallbackOneByOne_PermanentRejectionForOneItem_OthersStillSucceed()
    {
        var envelopes = new List<IngestEnvelope> { SampleEnvelope("evt-good"), SampleEnvelope("evt-bad") };
        var (sink, _) = CreateSink(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("events-batch"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = request.Content!.ReadAsStringAsync().Result;
            return body.Contains("evt-bad")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad envelope") }
                : OkWithBody();
        });

        var results = await sink.SubmitBatchAsync(envelopes, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results.Single(r => r.WebhookEventId == "evt-good").PermanentlyRejected);
        Assert.True(results.Single(r => r.WebhookEventId == "evt-bad").PermanentlyRejected);
    }
}
