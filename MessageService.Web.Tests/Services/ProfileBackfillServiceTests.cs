using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class ProfileBackfillServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly FakeProfileRefreshQueue _queue = new();
    private readonly FakeProfileStore _store = new();
    private readonly TestLogger<ProfileBackfillService> _logger = new();

    private ProfileBackfillService CreateService(ProfileCacheOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IProfileStore>(_ => _store);
        var provider = services.BuildServiceProvider();

        return new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _queue,
            OptionsFactory.Create(options ?? new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7), BackfillMaxPerScan = 100 }),
            _time,
            _logger);
    }

    [Fact]
    public async Task RunBackfillAsync_EnqueuesStaleProfiles_AndLogsGroupAndMemberCounts()
    {
        // 8. 背景掃描：回傳幾筆就入列幾筆，且群組與成員的 log 計數正確。
        var now = DateTimeOffset.UtcNow;
        _time.Now = now;

        _store.StaleProfilesToReturn =
        [
            new ProfileRefreshTask("g_stale", null),
            new ProfileRefreshTask("g_member_1", "u_member_1"),
            new ProfileRefreshTask("g_member_2", "u_member_2")
        ];

        var service = CreateService();
        await service.RunBackfillAsync(CancellationToken.None);

        Assert.Equal(3, _queue.Enqueued.Count);
        Assert.Contains(_queue.Enqueued, t => t.GroupId == "g_stale" && t.UserId == null);
        Assert.Contains(_queue.Enqueued, t => t.GroupId == "g_member_1" && t.UserId == "u_member_1");
        Assert.Contains(_queue.Enqueued, t => t.GroupId == "g_member_2" && t.UserId == "u_member_2");

        var call = Assert.Single(_store.GetStaleProfilesCalls);
        Assert.Equal(100, call.Max);
        Assert.Equal(now - TimeSpan.FromDays(7), call.Cutoff);

        Assert.Contains(_logger.Messages, m => m.Contains("Profile backfill enqueued 1 group(s) and 2 member(s)."));
    }

    [Fact]
    public async Task RunBackfillAsync_CandidatesExceedLimit_EnqueuesExactLimit()
    {
        // 9. 背景掃描：候選超過上限時，入列筆數等於上限。
        var now = DateTimeOffset.UtcNow;
        _time.Now = now;

        for (var i = 0; i < 10; i++)
        {
            _store.StaleProfilesToReturn.Add(new ProfileRefreshTask($"g_{i}", i % 2 == 0 ? null : $"u_{i}"));
        }

        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7),
            BackfillMaxPerScan = 3
        });

        await service.RunBackfillAsync(CancellationToken.None);

        var call = Assert.Single(_store.GetStaleProfilesCalls);
        Assert.Equal(3, call.Max);
        Assert.Equal(3, _queue.Enqueued.Count);
        Assert.Contains(_logger.Messages, m => m.Contains("Profile backfill enqueued 2 group(s) and 1 member(s)."));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunBackfillAsync_MaxPerScanZeroOrNegative_DoesNothing(int maxPerScan)
    {
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7),
            BackfillMaxPerScan = maxPerScan
        });

        await service.RunBackfillAsync(CancellationToken.None);

        Assert.Empty(_store.GetStaleProfilesCalls);
        Assert.Empty(_queue.Enqueued);
    }
}
