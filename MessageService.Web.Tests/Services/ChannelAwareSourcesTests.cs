using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

/// <summary>Auto 模式下推送不通時，媒體與名稱／頭貼要跟著改用「等 Core 來拿」的那組資源——
/// 只有訊息與心跳反轉的話，這兩條流會繼續往打不通的 Core 送而靜默失效。</summary>
public class ChannelAwareSourcesTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("推送不通時不該再打 Core 的 ingest API。");
    }

    private static (ChannelAwareContentWorkSource Content, ChannelAwareProfileStore Profile,
        EdgeChannelState State, FakeTimeProvider Time, EdgeContentStaging Staging, EdgeProfileStaging ProfileStaging)
        Create(IngestChannel channel = IngestChannel.Auto)
    {
        var time = new FakeTimeProvider();
        var ingest = OptionsFactory.Create(new IngestOptions { Channel = channel, PullActivationSeconds = 180 });
        var state = new EdgeChannelState(
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }), ingest, time);
        var staging = new EdgeContentStaging(ingest);
        var profileStaging = new EdgeProfileStaging();

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory, ThrowingHttpClientFactory>();
        services.AddScoped<ApiContentWorkSource>();
        services.AddScoped<ApiProfileStore>();
        var provider = services.BuildServiceProvider();

        return (
            new ChannelAwareContentWorkSource(state, new StagingContentWorkSource(staging), provider),
            new ChannelAwareProfileStore(state, new StagingProfileStore(profileStaging), provider),
            state, time, staging, profileStaging);
    }

    private static void PausePush(EdgeChannelState state, FakeTimeProvider time)
    {
        state.MarkPushFailed();
        time.Now = time.Now.AddSeconds(181);
        state.MarkPushFailed();
    }

    [Fact]
    public async Task ContentWorkSource_WhilePushHealthy_UsesApiPath()
    {
        var (content, _, _, _, _, _) = Create();

        // 推送還通的時候照原本打 Core 的 ingest API——這裡的假工廠會丟例外，證明真的走了那條路
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => content.GetPendingIdsAsync(true, null, "owner", CancellationToken.None));
    }

    [Fact]
    public async Task ContentWorkSource_AfterPushPauses_UsesStaging()
    {
        var (content, _, state, time, staging, _) = Create();
        staging.AcceptDispatch([new ContentWorkItem(7L, "msg-7", "image")]);

        PausePush(state, time);

        var ids = await content.GetPendingIdsAsync(true, null, "owner", CancellationToken.None);
        Assert.Equal([7L], ids);
    }

    [Fact]
    public async Task ContentWorkSource_AfterPushRecovers_GoesBackToApiPath()
    {
        var (content, _, state, time, _, _) = Create();
        PausePush(state, time);
        state.MarkPushSucceeded();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => content.GetPendingIdsAsync(true, null, "owner", CancellationToken.None));
    }

    [Fact]
    public async Task ProfileStore_AfterPushPauses_UsesStaging()
    {
        var (_, profile, state, time, _, profileStaging) = Create();
        profileStaging.Dispatch([new EdgeProfileWorkItem("G1", "U1", new ProfileStaleness(true, true))]);

        PausePush(state, time);

        var staleness = await profile.GetStalenessAsync("G1", "U1", DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.True(staleness.GroupStale);

        await profile.UpsertGroupAsync("G1", new GroupSummary("G1", "名稱", null), CancellationToken.None);
        Assert.Single(profileStaging.DrainResults());
    }

    [Fact]
    public async Task PullChannel_AlwaysUsesStaging()
    {
        var (content, profile, _, _, staging, profileStaging) = Create(IngestChannel.Pull);
        staging.AcceptDispatch([new ContentWorkItem(7L, "msg-7", "image")]);
        profileStaging.Dispatch([new EdgeProfileWorkItem("G1", null, new ProfileStaleness(true, false))]);

        Assert.Equal([7L], await content.GetPendingIdsAsync(true, null, "owner", CancellationToken.None));
        Assert.True((await profile.GetStalenessAsync("G1", null, DateTimeOffset.UnixEpoch, CancellationToken.None)).GroupStale);
    }
}
