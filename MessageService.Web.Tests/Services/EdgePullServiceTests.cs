using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MessageService.Controllers;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class EdgePullServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>只記錄有沒有寫過 log 與寫了什麼等級，用來驗證「正常輪詢不留 log」。</summary>
    private sealed class RecordingLogger : ILogger<EdgePullService>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://edge.example/") };
    }

    private sealed record Harness(
        EdgePullService Service,
        FakeTimeProvider Time,
        PushHeartbeatTracker Tracker,
        FakeHeartbeatStore HeartbeatStore,
        FakeIngestSink Sink,
        FakeContentDownloadQueue DownloadQueue,
        FakeContentWorkSource WorkSource,
        List<HttpRequestMessage> Requests,
        List<string> RequestBodies,
        RecordingLogger Logger);

    private static readonly IngestEnvelope SampleEnvelope = new(
        WebhookEventId: "evt-1",
        LineMessageId: "msg-1",
        GroupId: "G1",
        UserId: "U1",
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.UnixEpoch,
        ReceivedAt: DateTimeOffset.UnixEpoch,
        HasContent: false,
        ContentFileName: null);

    private static Harness CreateHarness(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        IngestOptions? options = null,
        FakeIngestSink? sink = null,
        FakeContentWorkSource? workSource = null)
    {
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            requests.Add(request);
            bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return responder(request);
        });

        var time = new FakeTimeProvider();
        var tracker = new PushHeartbeatTracker(time);
        var heartbeatStore = new FakeHeartbeatStore();
        var actualSink = sink ?? new FakeIngestSink();
        var downloadQueue = new FakeContentDownloadQueue();
        var actualWorkSource = workSource ?? new FakeContentWorkSource();

        var services = new ServiceCollection();
        services.AddSingleton(ProcessOwnerId.Instance);
        services.AddScoped<IContentWorkSource>(_ => actualWorkSource);
        services.AddScoped<IHeartbeatStore>(_ => heartbeatStore);
        services.AddScoped<IIngestSink>(_ => actualSink);
        services.AddScoped<IContentDownloadQueue>(_ => downloadQueue);
        services.AddScoped<IProfileRefreshQueue>(_ => new FakeProfileRefreshQueue());
        var provider = services.BuildServiceProvider();

        var logger = new RecordingLogger();
        var service = new EdgePullService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(handler),
            tracker,
            time,
            OptionsFactory.Create(options ?? new IngestOptions
            {
                EdgeBaseUrl = "https://edge.example/",
                ApiKey = "k",
            }),
            logger);

        return new Harness(
            service, time, tracker, heartbeatStore, actualSink, downloadQueue, actualWorkSource, requests, bodies, logger);
    }

    private static HttpResponseMessage PollResponse(
        string role = "Edge", string machineName = "EDGE01",
        long? outboxPending = 0, double? oldestAge = null,
        IReadOnlyList<EdgeOutboxItem>? messages = null,
        IReadOnlyList<long>? readyContentIds = null,
        IReadOnlyList<long>? failedContentIds = null,
        IReadOnlyList<long>? acceptedContentWork = null) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new EdgePollResponse(
                role, machineName, outboxPending, oldestAge, messages ?? [],
                acceptedContentWork ?? [], readyContentIds ?? [], failedContentIds ?? [])),
        };

    // === 啟停判斷 ===

    [Fact]
    public void ShouldPull_NeverReceivedPushHeartbeat_ReturnsTrue()
    {
        var harness = CreateHarness(_ => PollResponse());

        // 從未收過推送心跳＝edge→core 從一開始就不通，不必等門檻
        Assert.True(harness.Service.ShouldPull());
    }

    [Fact]
    public void ShouldPull_PushHeartbeatWithinThreshold_ReturnsFalse()
    {
        var harness = CreateHarness(_ => PollResponse());
        harness.Tracker.MarkReceived();
        harness.Time.Now = harness.Time.Now.AddSeconds(179);

        Assert.False(harness.Service.ShouldPull());
    }

    [Fact]
    public void ShouldPull_PushHeartbeatOlderThanThreshold_ReturnsTrue()
    {
        var harness = CreateHarness(_ => PollResponse());
        harness.Tracker.MarkReceived();
        harness.Time.Now = harness.Time.Now.AddSeconds(181);

        Assert.True(harness.Service.ShouldPull());
    }

    [Fact]
    public async Task PollOnceAsync_PushHeartbeatAlive_DoesNotSendRequest()
    {
        var harness = CreateHarness(_ => PollResponse());
        harness.Tracker.MarkReceived();

        var polled = await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.False(polled);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task PollOnceAsync_PulledHeartbeatDoesNotStopSubsequentPolling()
    {
        // 自我震盪防護：輪詢拉回來的心跳寫進 HostHeartbeats，但不得刷新 PushHeartbeatTracker，
        // 否則第二輪會誤判推送已恢復而停止輪詢
        var harness = CreateHarness(_ => PollResponse());

        Assert.True(await harness.Service.PollOnceAsync(CancellationToken.None));
        Assert.True(await harness.Service.PollOnceAsync(CancellationToken.None));

        Assert.Null(harness.Tracker.LastReceivedAt);
        Assert.Equal(2, harness.Requests.Count);
    }

    // === 心跳落地 ===

    [Fact]
    public async Task PollOnceAsync_WritesHeartbeatWithNullFingerprint()
    {
        var harness = CreateHarness(_ => PollResponse(outboxPending: 7, oldestAge: 12.5));

        await harness.Service.PollOnceAsync(CancellationToken.None);

        var record = Assert.Single(harness.HeartbeatStore.Upserted);
        Assert.Equal("Edge", record.Role);
        Assert.Equal("EDGE01", record.MachineName);
        Assert.Equal(7, record.Report.OutboxPending);
        Assert.Equal(12.5, record.Report.OutboxOldestAgeSeconds);
        Assert.Null(record.EncryptionKeyFingerprint);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("Bogus")]
    [InlineData("")]
    public async Task PollOnceAsync_InvalidRole_SkipsHeartbeatWithoutThrowing(string role)
    {
        var harness = CreateHarness(_ => PollResponse(role: role));

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.HeartbeatStore.Upserted);
    }

    [Fact]
    public async Task PollOnceAsync_MachineNameTooLong_SkipsHeartbeat()
    {
        var harness = CreateHarness(_ => PollResponse(machineName: new string('x', 129)));

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.HeartbeatStore.Upserted);
    }

    // === 訊息落地與 ack ===

    [Fact]
    public async Task PollOnceAsync_LandsMessagesAndAcksThem()
    {
        var item = new EdgeOutboxItem("evt-1", JsonSerializer.Serialize(SampleEnvelope));
        var harness = CreateHarness(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/poll")
                ? PollResponse(messages: [item])
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("evt-1", Assert.Single(harness.Sink.Submitted).WebhookEventId);

        var ackBody = harness.RequestBodies[^1];
        Assert.Contains("evt-1", ackBody);
        Assert.EndsWith("/api/edge/outbox/ack", harness.Requests[^1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PollOnceAsync_NoMessages_DoesNotSendAck()
    {
        var harness = CreateHarness(_ => PollResponse());

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Single(harness.Requests);
        Assert.EndsWith("/api/edge/poll", harness.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PollOnceAsync_UnreadablePayload_SkippedAndNotAcked()
    {
        var harness = CreateHarness(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/poll")
                ? PollResponse(messages: [new EdgeOutboxItem("evt-bad", "{ not json")])
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Sink.Submitted);
        // 只有 poll，沒有 ack——壞掉的 payload 留在 Edge 讓人工處理
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task PollOnceAsync_PermanentlyRejected_IsStillAcked()
    {
        var sink = new FakeIngestSink();
        sink.ThrowForWebhookEventId["evt-1"] = new PermanentIngestException("payload 不合法");
        var item = new EdgeOutboxItem("evt-1", JsonSerializer.Serialize(SampleEnvelope));
        var harness = CreateHarness(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/poll")
                ? PollResponse(messages: [item])
                : new HttpResponseMessage(HttpStatusCode.NoContent), sink: sink);

        await harness.Service.PollOnceAsync(CancellationToken.None);

        // 永久拒絕的留在 Edge 只會無限重送，一樣要 ack
        Assert.Contains("evt-1", harness.RequestBodies[^1]);
        // 但不套用副作用（不入列下載）
        Assert.Empty(harness.DownloadQueue.Enqueued);
    }

    [Fact]
    public async Task PollOnceAsync_RepeatedDelivery_AcksBothTimes()
    {
        var item = new EdgeOutboxItem("evt-1", JsonSerializer.Serialize(SampleEnvelope));
        var harness = CreateHarness(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/poll")
                ? PollResponse(messages: [item])
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        await harness.Service.PollOnceAsync(CancellationToken.None);
        await harness.Service.PollOnceAsync(CancellationToken.None);

        // 落地端靠 WebhookEventId 唯一索引去重，重複投遞安全，兩次都要 ack
        Assert.Equal(2, harness.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/outbox/ack")));
    }

    // === 失敗退避與日誌紀律 ===

    [Fact]
    public async Task PollOnceAsync_Failure_BacksOffExponentiallyUpToCap()
    {
        var harness = CreateHarness(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(TimeSpan.FromSeconds(1), harness.Service.CurrentDelay());

        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(1), harness.Service.CurrentDelay());

        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(2), harness.Service.CurrentDelay());

        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(4), harness.Service.CurrentDelay());

        for (var i = 0; i < 10; i++)
        {
            await harness.Service.PollOnceAsync(CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), harness.Service.CurrentDelay());
    }

    [Fact]
    public async Task PollOnceAsync_RecoversAfterFailure_ReturnsToNormalInterval()
    {
        var fail = true;
        var harness = CreateHarness(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : PollResponse());

        await harness.Service.PollOnceAsync(CancellationToken.None);
        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.True(harness.Service.CurrentDelay() > TimeSpan.FromSeconds(1));

        fail = false;
        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), harness.Service.CurrentDelay());
    }

    [Fact]
    public async Task PollOnceAsync_ContinuousFailures_DoNotLogEveryTime()
    {
        var harness = CreateHarness(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        for (var i = 0; i < 20; i++)
        {
            await harness.Service.PollOnceAsync(CancellationToken.None);
        }

        // 1 秒一次的失敗如果每次都記，一天會多出 8 萬行——只允許「開始輪詢」與「進入退避」兩則
        Assert.Equal(1, harness.Logger.Records.Count(r => r.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task PollOnceAsync_ContinuousFailures_LogsSummaryEveryTenMinutes()
    {
        var harness = CreateHarness(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await harness.Service.PollOnceAsync(CancellationToken.None);
        harness.Time.Now = harness.Time.Now.AddMinutes(11);
        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, harness.Logger.Records.Count(r => r.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task PollOnceAsync_SuccessfulPolls_ProduceNoLogsBeyondStartTransition()
    {
        var harness = CreateHarness(_ => PollResponse());

        for (var i = 0; i < 5; i++)
        {
            await harness.Service.PollOnceAsync(CancellationToken.None);
        }

        // 只有第一次的「開始輪詢」狀態轉換，之後每次成功都不得留下 log
        Assert.Single(harness.Logger.Records);
        Assert.Equal(LogLevel.Information, harness.Logger.Records[0].Level);
    }

    [Fact]
    public async Task PollOnceAsync_PushResumes_LogsStopTransitionOnce()
    {
        var harness = CreateHarness(_ => PollResponse());

        await harness.Service.PollOnceAsync(CancellationToken.None);
        harness.Tracker.MarkReceived();
        await harness.Service.PollOnceAsync(CancellationToken.None);
        await harness.Service.PollOnceAsync(CancellationToken.None);

        // 開始輪詢 1 則 + 停止輪詢 1 則，第三次不再重複記
        Assert.Equal(2, harness.Logger.Records.Count);
    }

    // === 媒體 blob 取回（作業C） ===

    private static HttpResponseMessage ContentResponse(byte[] bytes, string contentType = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        response.Content.Headers.ContentLength = bytes.LongLength;
        return response;
    }

    [Fact]
    public async Task PollOnceAsync_DispatchesPendingContentWork()
    {
        var workSource = new FakeContentWorkSource { PendingIds = [7L] };
        workSource.Items[7L] = new ContentWorkItem(7L, "msg-7", "image");
        var harness = CreateHarness(_ => PollResponse(), workSource: workSource);

        await harness.Service.PollOnceAsync(CancellationToken.None);

        // 派工隨 poll request 一起送出，Core 端的認領／租約仍由既有 work source 負責
        Assert.Contains("\"contentId\":7", harness.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(true, workSource.ReclaimDownloadingCalls);
    }

    [Fact]
    public async Task PollOnceAsync_ReadyContent_IsFetchedLandedAndAcked()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var workSource = new FakeContentWorkSource();
        var harness = CreateHarness(request =>
            request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/poll") => PollResponse(readyContentIds: [9L]),
                var p when p.EndsWith("/content/9") => ContentResponse(payload),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            }, workSource: workSource);

        await harness.Service.PollOnceAsync(CancellationToken.None);

        var completed = Assert.Single(workSource.Completed);
        Assert.Equal(9L, completed.ContentId);
        Assert.Equal(payload, completed.Content);
        Assert.Equal("image/png", completed.ContentType);

        // 完整落地之後才 ack，Edge 這時才釋放暫存
        Assert.EndsWith("/api/edge/content/9/ack", harness.Requests[^1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PollOnceAsync_TruncatedContent_IsNotLandedAndNotAcked()
    {
        var workSource = new FakeContentWorkSource();
        var harness = CreateHarness(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll"))
            {
                return PollResponse(readyContentIds: [9L]);
            }

            // 宣告 100 位元組卻只給 5 個：模擬傳輸被截斷
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[5]),
            };
            response.Content.Headers.ContentLength = 100;
            return response;
        }, workSource: workSource);

        await harness.Service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(workSource.Completed);
        Assert.DoesNotContain(harness.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/ack"));
    }

    [Fact]
    public async Task PollOnceAsync_ContentFetchFails_RetriesOnNextRound()
    {
        var fail = true;
        var workSource = new FakeContentWorkSource();
        var harness = CreateHarness(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll"))
            {
                return PollResponse(readyContentIds: [9L]);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/content/9"))
            {
                return fail
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : ContentResponse([1, 2, 3]);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }, workSource: workSource);

        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.Empty(workSource.Completed);

        // 取回失敗不 ack，Edge 保留暫存，下一輪重取就會成功
        fail = false;
        await harness.Service.PollOnceAsync(CancellationToken.None);
        Assert.Single(workSource.Completed);
    }

    [Fact]
    public async Task PollOnceAsync_SlowFetch_IsNotRequestedTwiceConcurrently()
    {
        var gate = new TaskCompletionSource();
        var fetchCount = 0;
        var workSource = new FakeContentWorkSource();
        var harness = CreateHarness(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll"))
            {
                return PollResponse(readyContentIds: [9L]);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/content/9"))
            {
                Interlocked.Increment(ref fetchCount);
                gate.Task.GetAwaiter().GetResult();
                return ContentResponse([1, 2, 3]);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }, workSource: workSource);

        // 取回耗時超過輪詢間隔時，第二輪不得對同一筆再發一次 GET
        var first = Task.Run(() => harness.Service.PollOnceAsync(CancellationToken.None));
        while (Volatile.Read(ref fetchCount) == 0)
        {
            await Task.Yield();
        }

        await harness.Service.PollOnceAsync(CancellationToken.None);
        gate.SetResult();
        await first;

        Assert.Equal(1, Volatile.Read(ref fetchCount));
    }

    [Fact]
    public async Task PollOnceAsync_FailedContentIds_GoThroughExistingRetryStateMachine()
    {
        var workSource = new FakeContentWorkSource();
        var harness = CreateHarness(_ => PollResponse(failedContentIds: [5L]), workSource: workSource);

        await harness.Service.PollOnceAsync(CancellationToken.None);

        // 不疊第二套死信：交回既有的 MaxRetries／Failed 狀態機
        Assert.Equal(5L, Assert.Single(workSource.Failed).ContentId);
    }

    [Fact]
    public async Task PollOnceAsync_InFlightContent_IsNotDispatchedAgain()
    {
        var gate = new TaskCompletionSource();
        var workSource = new FakeContentWorkSource { PendingIds = [9L] };
        workSource.Items[9L] = new ContentWorkItem(9L, "msg-9", "image");
        var harness = CreateHarness(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll"))
            {
                return PollResponse(readyContentIds: [9L]);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/content/9"))
            {
                gate.Task.GetAwaiter().GetResult();
                return ContentResponse([1, 2, 3]);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }, workSource: workSource);

        var first = Task.Run(() => harness.Service.PollOnceAsync(CancellationToken.None));
        while (harness.Requests.Count < 2)
        {
            await Task.Yield();
        }

        await harness.Service.PollOnceAsync(CancellationToken.None);
        gate.SetResult();
        await first;

        // 正在取回中的那筆不該被重新派工——內容已經在傳回來的路上
        var secondPollBody = harness.RequestBodies.Last(b => b.Contains("contentWork", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("\"contentId\":9", secondPollBody, StringComparison.OrdinalIgnoreCase);
    }
}
