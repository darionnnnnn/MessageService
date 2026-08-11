using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Outbox;

public class OutboxForwarderServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeIngestSink _sink = new();

    public OutboxForwarderServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IIngestSink>(_sink);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private OutboxForwarderService CreateForwarder(OutboxOptions? options = null) =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeOutboxSignal(),
            OptionsFactory.Create(options ?? new OutboxOptions
            {
                BatchSize = 50,
                BaseRetryDelaySeconds = 5,
                MaxRetryDelaySeconds = 300
            }),
            NullLogger<OutboxForwarderService>.Instance);

    private static IngestEnvelope SampleEnvelope(string webhookEventId = "evt-1") => new(
        WebhookEventId: webhookEventId,
        LineMessageId: "m1",
        GroupId: "G1",
        UserId: "U1",
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
        ReceivedAt: DateTimeOffset.UtcNow,
        HasContent: false,
        ContentFileName: null);

    private async Task<OutboxEntry> SeedEntryAsync(IngestEnvelope? envelope = null, DateTimeOffset? nextAttemptAt = null, int attempts = 0)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        var entry = new OutboxEntry
        {
            WebhookEventId = (envelope ?? SampleEnvelope()).WebhookEventId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(envelope ?? SampleEnvelope()),
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = nextAttemptAt,
            Attempts = attempts
        };
        dbContext.Entries.Add(entry);
        await dbContext.SaveChangesAsync();
        return entry;
    }

    private async Task<List<OutboxEntry>> GetRemainingEntriesAsync()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        return await dbContext.Entries.ToListAsync();
    }

    [Fact]
    public async Task ProcessBatchAsync_EmptyOutbox_ReturnsFalse()
    {
        var forwarder = CreateForwarder();

        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.False(processedAny);
    }

    [Fact]
    public async Task ProcessBatchAsync_DueEntry_SubmitsToSinkAndRemovesFromOutbox()
    {
        await SeedEntryAsync(SampleEnvelope("evt-1"));
        var forwarder = CreateForwarder();

        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.True(processedAny);
        var submitted = Assert.Single(_sink.Submitted);
        Assert.Equal("evt-1", submitted.WebhookEventId);
        Assert.Empty(await GetRemainingEntriesAsync());
    }

    [Fact]
    public async Task ProcessBatchAsync_SinkThrows_KeepsEntryAndSchedulesRetry()
    {
        await SeedEntryAsync(SampleEnvelope("evt-1"));
        _sink.ThrowOnNextSubmit = new InvalidOperationException("backend unreachable");
        var forwarder = CreateForwarder(new OutboxOptions { BatchSize = 50, BaseRetryDelaySeconds = 5, MaxRetryDelaySeconds = 300 });

        var before = DateTimeOffset.UtcNow;
        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.True(processedAny); // 有嘗試處理，即使失敗——"processedAny" 代表這輪不是空手而回
        Assert.Empty(_sink.Submitted);
        var remaining = Assert.Single(await GetRemainingEntriesAsync());
        Assert.Equal(1, remaining.Attempts);
        Assert.NotNull(remaining.NextAttemptAt);
        Assert.True(remaining.NextAttemptAt > before, "重試時間應該被排到未來，不是立刻可再處理");
        Assert.Contains("backend unreachable", remaining.LastError);
    }

    [Fact]
    public async Task ProcessBatchAsync_EntryNotYetDue_IsNotPickedUp()
    {
        await SeedEntryAsync(SampleEnvelope("evt-1"), nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(10));
        var forwarder = CreateForwarder();

        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.False(processedAny);
        Assert.Empty(_sink.Submitted);
        Assert.Single(await GetRemainingEntriesAsync());
    }

    [Fact]
    public async Task ProcessBatchAsync_EntryPastDue_IsPickedUp()
    {
        await SeedEntryAsync(SampleEnvelope("evt-1"), nextAttemptAt: DateTimeOffset.UtcNow.AddSeconds(-1), attempts: 2);
        var forwarder = CreateForwarder();

        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.True(processedAny);
        Assert.Single(_sink.Submitted);
    }

    [Fact]
    public async Task ProcessBatchAsync_OneEntryThrows_OthersInSameBatchStillProcessed()
    {
        await SeedEntryAsync(SampleEnvelope("evt-fails"));
        await SeedEntryAsync(SampleEnvelope("evt-ok"));
        // Dictionary/List 順序取決於 Id 遞增，Id 較小的（先插入的）先跑到；讓先跑到的那個失敗
        _sink.ThrowOnNextSubmit = new InvalidOperationException("boom");
        var forwarder = CreateForwarder();

        await forwarder.ProcessBatchAsync(CancellationToken.None);

        var submitted = Assert.Single(_sink.Submitted);
        Assert.Equal("evt-ok", submitted.WebhookEventId);
        var remaining = Assert.Single(await GetRemainingEntriesAsync());
        Assert.Equal("evt-fails", remaining.WebhookEventId);
    }

    [Fact]
    public async Task ProcessBatchAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedEntryAsync(SampleEnvelope($"evt-{i}"));
        }
        var forwarder = CreateForwarder(new OutboxOptions { BatchSize = 2, BaseRetryDelaySeconds = 5, MaxRetryDelaySeconds = 300 });

        var processedAny = await forwarder.ProcessBatchAsync(CancellationToken.None);

        Assert.True(processedAny);
        Assert.Equal(2, _sink.Submitted.Count);
        Assert.Equal(3, (await GetRemainingEntriesAsync()).Count);
    }

    [Fact]
    public async Task ProcessBatchAsync_RetryDelay_IsCappedAtMax()
    {
        // 第 100 次嘗試：BaseRetryDelaySeconds(5) × 100 = 500，遠超過 MaxRetryDelaySeconds(300)，應該封頂
        await SeedEntryAsync(SampleEnvelope("evt-1"), attempts: 99);
        _sink.ThrowOnNextSubmit = new InvalidOperationException("still down");
        var forwarder = CreateForwarder(new OutboxOptions { BatchSize = 50, BaseRetryDelaySeconds = 5, MaxRetryDelaySeconds = 300 });

        var before = DateTimeOffset.UtcNow;
        await forwarder.ProcessBatchAsync(CancellationToken.None);

        var remaining = Assert.Single(await GetRemainingEntriesAsync());
        var delay = remaining.NextAttemptAt!.Value - before;
        Assert.True(delay.TotalSeconds <= 300 + 2, $"延遲應該被封頂在約 300 秒，實際 {delay.TotalSeconds}");
    }
}
