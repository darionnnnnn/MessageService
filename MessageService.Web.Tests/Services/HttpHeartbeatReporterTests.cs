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
    public async Task ReportAsync_Fails_DoesNotAffectChannelState()
    {
        var (reporter, state, _) = Create(System.Net.HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reporter.ReportAsync(new HeartbeatReport(0, null), CancellationToken.None));

        // 失敗側刻意不通知：心跳偶發失敗不該把轉發拖入暫停
        Assert.False(state.PushPaused);
    }
}
