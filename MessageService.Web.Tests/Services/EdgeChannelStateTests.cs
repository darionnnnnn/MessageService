using MessageService.Options;
using MessageService.Services;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class EdgeChannelStateTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (EdgeChannelState State, FakeTimeProvider Time) Create(
        IngestChannel channel = IngestChannel.Auto, int probeMinutes = 60,
        DeploymentMode mode = DeploymentMode.Edge, int pauseAfterSeconds = 180)
    {
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = mode }),
            OptionsFactory.Create(new IngestOptions
            {
                Channel = channel,
                ChannelProbeIntervalMinutes = probeMinutes,
                PullActivationSeconds = pauseAfterSeconds,
            }),
            time);
        return (state, time);
    }

    /// <summary>推送失敗超過寬限期才會真的暫停（寬限期內沿用 outbox 自己的秒級退避）。</summary>
    private static void FailPastGrace(EdgeChannelState state, FakeTimeProvider time, int pauseAfterSeconds = 180)
    {
        state.MarkPushFailed();
        time.Now = time.Now.AddSeconds(pauseAfterSeconds + 1);
        state.MarkPushFailed();
    }

    [Theory]
    [InlineData(IngestChannel.Auto, true)]
    [InlineData(IngestChannel.Push, true)]
    [InlineData(IngestChannel.Pull, false)]
    public void PushConfigured_DependsOnChannel(IngestChannel channel, bool expected)
    {
        var (state, _) = Create(channel);

        Assert.Equal(expected, state.PushConfigured);
    }

    [Fact]
    public void ShouldAttemptPush_PullChannel_NeverAttempts()
    {
        var (state, time) = Create(IngestChannel.Pull);

        Assert.False(state.ShouldAttemptPush());

        // 就算過了很久也不會忽然開始探測——明知封死的方向不做無謂連線
        time.Now = time.Now.AddDays(7);
        Assert.False(state.ShouldAttemptPush());
    }

    [Fact]
    public void ShouldAttemptPush_Healthy_AlwaysAttempts()
    {
        var (state, _) = Create();

        Assert.True(state.ShouldAttemptPush());
        Assert.True(state.ShouldAttemptPush());
        Assert.False(state.PushPaused);
    }

    [Fact]
    public void MarkPushFailed_WithinGracePeriod_DoesNotPauseYet()
    {
        var (state, time) = Create();

        // 短暫失敗（Core 重啟、IIS 回收、網路抖動）沿用 outbox 自己的秒級退避，
        // 不該讓 Edge 停推一小時——純推送部署沒有輪詢器接手
        state.MarkPushFailed();
        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());

        time.Now = time.Now.AddSeconds(179);
        state.MarkPushFailed();
        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());
    }

    [Fact]
    public void MarkPushFailed_ThenSucceedsWithinGrace_ResetsFailureClock()
    {
        var (state, time) = Create();
        state.MarkPushFailed();
        time.Now = time.Now.AddSeconds(179);

        state.MarkPushSucceeded();

        // 恢復過就重新計算寬限期，不能讓久遠的第一次失敗把後來的短暫失敗直接推進暫停
        time.Now = time.Now.AddSeconds(10);
        state.MarkPushFailed();
        Assert.False(state.PushPaused);
    }

    [Theory]
    [InlineData(DeploymentMode.AllInOne)]
    [InlineData(DeploymentMode.Core)]
    public void NonEdgeModes_AreNeverGated(DeploymentMode mode)
    {
        var (state, time) = Create(mode: mode);

        // AllInOne 的 sink 是 DirectIngestSink，落地失敗是資料庫的問題，
        // 不該被通道閘門擋住一小時
        FailPastGrace(state, time);

        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());
        Assert.False(state.UsePullResources);
    }

    [Fact]
    public void ShouldAttemptPush_ProbeWithNoWork_DoesNotConsumeTheProbe()
    {
        var (state, time) = Create(probeMinutes: 60);
        FailPastGrace(state, time);
        time.Now = time.Now.AddMinutes(61);

        // 空批次送不出任何東西，拿它當探測會讓計時白白重置
        Assert.True(state.ShouldAttemptPush(hasWorkToSend: false));
        Assert.True(state.ShouldAttemptPush(hasWorkToSend: true));
    }

    [Theory]
    [InlineData(IngestChannel.Pull, DeploymentMode.Edge, true)]
    [InlineData(IngestChannel.Auto, DeploymentMode.Edge, false)]
    [InlineData(IngestChannel.Push, DeploymentMode.Edge, false)]
    [InlineData(IngestChannel.Pull, DeploymentMode.AllInOne, false)]
    public void UsePullResources_BeforeAnyFailure(IngestChannel channel, DeploymentMode mode, bool expected)
    {
        var (state, _) = Create(channel, mode: mode);

        Assert.Equal(expected, state.UsePullResources);
    }

    [Fact]
    public void UsePullResources_TurnsOnWhenPushPauses()
    {
        var (state, time) = Create();
        Assert.False(state.UsePullResources);

        FailPastGrace(state, time);

        // 推送暫停時媒體與名稱／頭貼也要跟著改用暫存，不能繼續往打不通的 Core 送
        Assert.True(state.UsePullResources);

        state.MarkPushSucceeded();
        Assert.False(state.UsePullResources);
    }

    [Fact]
    public void ShouldAttemptPush_AfterFailure_PausesUntilProbeInterval()
    {
        var (state, time) = Create(probeMinutes: 60);
        FailPastGrace(state, time);

        Assert.True(state.PushPaused);
        Assert.False(state.ShouldAttemptPush());

        time.Now = time.Now.AddMinutes(59);
        Assert.False(state.ShouldAttemptPush());

        time.Now = time.Now.AddMinutes(2);
        Assert.True(state.ShouldAttemptPush());
    }

    [Fact]
    public void ShouldAttemptPush_ProbeLetThrough_DoesNotLetSecondOneThroughImmediately()
    {
        var (state, time) = Create(probeMinutes: 60);
        FailPastGrace(state, time);
        time.Now = time.Now.AddMinutes(61);

        Assert.True(state.ShouldAttemptPush());
        // 探測失敗後不得在同一個週期內被連續放行
        state.MarkPushFailed();
        Assert.False(state.ShouldAttemptPush());
    }

    [Fact]
    public void MarkPushSucceeded_ResumesNormalPushing()
    {
        var (state, time) = Create(probeMinutes: 60);
        FailPastGrace(state, time);
        time.Now = time.Now.AddMinutes(61);

        Assert.True(state.ShouldAttemptPush());
        state.MarkPushSucceeded();

        Assert.False(state.PushPaused);
        Assert.True(state.ShouldAttemptPush());
        Assert.True(state.ShouldAttemptPush());
    }

    [Fact]
    public void MarkPushFailed_Repeatedly_DoesNotPushProbeDeadlineFurtherOut()
    {
        var (state, time) = Create(probeMinutes: 60);
        FailPastGrace(state, time);

        // 暫停期間反覆回報失敗不該讓探測時點一直往後延，否則永遠等不到探測
        time.Now = time.Now.AddMinutes(30);
        state.MarkPushFailed();
        time.Now = time.Now.AddMinutes(31);

        Assert.True(state.ShouldAttemptPush());
    }
}
