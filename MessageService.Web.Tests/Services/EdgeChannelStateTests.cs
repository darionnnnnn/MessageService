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
        IngestChannel channel = IngestChannel.Auto, int probeMinutes = 60)
    {
        var time = new FakeTimeProvider();
        var state = new EdgeChannelState(
            OptionsFactory.Create(new IngestOptions { Channel = channel, ChannelProbeIntervalMinutes = probeMinutes }),
            time);
        return (state, time);
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
    public void ShouldAttemptPush_AfterFailure_PausesUntilProbeInterval()
    {
        var (state, time) = Create(probeMinutes: 60);
        state.MarkPushFailed();

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
        state.MarkPushFailed();
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
        state.MarkPushFailed();
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
        state.MarkPushFailed();

        // 暫停期間反覆回報失敗不該讓探測時點一直往後延，否則永遠等不到探測
        time.Now = time.Now.AddMinutes(30);
        state.MarkPushFailed();
        time.Now = time.Now.AddMinutes(31);

        Assert.True(state.ShouldAttemptPush());
    }
}
