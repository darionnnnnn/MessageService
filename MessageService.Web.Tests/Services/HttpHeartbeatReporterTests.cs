using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class HttpHeartbeatReporterTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://core.example/") };
    }

    private static (HttpHeartbeatReporter Reporter, EdgeChannelState State, FakeTimeProvider Time) Create(
        System.Net.HttpStatusCode status)
    {
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            OptionsFactory.Create(new IngestOptions { Channel = IngestChannel.Auto, PullActivationSeconds = 180 }),
            time);
        var reporter = new HttpHeartbeatReporter(
            new StubHttpClientFactory(new FakeHttpMessageHandler(_ => new HttpResponseMessage(status))),
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            state);
        return (reporter, state, time);
    }

    private static void PausePush(EdgeChannelState state, FakeTimeProvider time)
    {
        state.MarkPushFailed();
        time.Now = time.Now.AddSeconds(181);
        state.MarkPushFailed();
    }

    [Fact]
    public async Task ReportAsync_Succeeds_ResumesPausedPush()
    {
        var (reporter, state, time) = Create(System.Net.HttpStatusCode.NoContent);
        PausePush(state, time);
        Assert.True(state.PushPaused);

        // 心跳每分鐘照打、不經通道閘門——成功就代表方向通了，要立刻恢復轉發。
        // 否則 Core 收到推送心跳停止輪詢、Edge 卻還在暫停期，訊息會卡住最長一個探測週期
        await reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None);

        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());
    }

    [Fact]
    public async Task ReportAsync_FailsOnce_KeepsPushingDuringGracePeriod()
    {
        var (reporter, state, _) = Create(System.Net.HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        // 偶發失敗（Core 重啟、IIS 回收）不該把轉發拖進一小時的暫停——
        // PullActivationSeconds 的寬限期擋著，這時仍照常推送
        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());
    }

    [Fact]
    public async Task ReportAsync_FailsAcrossGracePeriod_PausesPushAndSwitchesToPullResources()
    {
        var (reporter, state, time) = Create(System.Net.HttpStatusCode.ServiceUnavailable);

        // 沒有訊息流量時 outbox 根本不會嘗試推送，心跳是唯一固定送出的流量——
        // 它不通知通道狀態的話，安靜的站台永遠不會切到拉取資源（實測到的症狀）
        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));
        time.Now = time.Now.AddSeconds(181);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        Assert.True(state.PushPaused);
        Assert.True(state.UsePullResources);
    }

    [Fact]
    public async Task ReportAsync_Fails4xx_AlsoCountsAsFailure()
    {
        // 語意是「推送通道未確認可用」，不分辨連線層與應用層失敗：
        // ingest 金鑰錯的話這個方向一樣送不到，該讓 Core 的輪詢接手
        var (reporter, state, time) = Create(System.Net.HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));
        time.Now = time.Now.AddSeconds(181);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        Assert.True(state.PushPaused);
    }

    /// <summary>HttpClient 逾時是 TaskCanceledException：Core 端 DROP 封包而 TCP 連線逾時比 client 的
    /// 30 秒長時（Linux、半開連線）看到的就是這一種。用例外型別過濾會讓心跳「送不到」不計入，
    /// 通道永遠不切換——批次 A 的修正在那個情境下等於沒做。</summary>
    [Fact]
    public async Task ReportAsync_HttpClientTimeout_CountsAsFailure()
    {
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            OptionsFactory.Create(new IngestOptions { Channel = IngestChannel.Auto, PullActivationSeconds = 180 }),
            time);
        var reporter = new HttpHeartbeatReporter(
            new StubHttpClientFactory(new FakeHttpMessageHandler(_ =>
                throw new TaskCanceledException("timeout", new TimeoutException()))),
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            state);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));
        time.Now = time.Now.AddSeconds(181);
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        Assert.True(state.PushPaused);
    }

    [Fact]
    public async Task ReportAsync_CallerCancelled_DoesNotCountAsFailure()
    {
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            OptionsFactory.Create(new IngestOptions { Channel = IngestChannel.Auto, PullActivationSeconds = 180 }),
            time);
        using var cts = new CancellationTokenSource();
        var reporter = new HttpHeartbeatReporter(
            new StubHttpClientFactory(new FakeHttpMessageHandler(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            })),
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            state);

        // 停機中的取消：兩次跨過寬限期也不能進入暫停
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), cts.Token));
        time.Now = time.Now.AddSeconds(181);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), cts.Token));

        Assert.False(state.PushPaused);
    }

    [Fact]
    public async Task ReportAsync_SucceedsAfterFailures_ClearsFailureStreak()
    {
        // 防火牆重新開通後要能升級回推送：成功一次就清掉累積的失敗時點，
        // 之後再失敗一次也不該立刻暫停（寬限期重新起算）
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            OptionsFactory.Create(new IngestOptions { Channel = IngestChannel.Auto, PullActivationSeconds = 180 }),
            time);
        var status = System.Net.HttpStatusCode.ServiceUnavailable;
        var reporter = new HttpHeartbeatReporter(
            new StubHttpClientFactory(new FakeHttpMessageHandler(_ => new HttpResponseMessage(status))),
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            state);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        status = System.Net.HttpStatusCode.NoContent;
        await reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None);

        status = System.Net.HttpStatusCode.ServiceUnavailable;
        time.Now = time.Now.AddSeconds(181);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        Assert.False(state.PushPaused);
    }
}
