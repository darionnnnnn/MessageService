using System.Text.Json;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Outbox;

public class SqliteOutboxWriterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeOutboxSignal _signal = new();

    public SqliteOutboxWriterTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IOutboxSignal>(_signal);
        services.AddScoped<IOutboxWriter, SqliteOutboxWriter>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private static IngestEnvelope SampleEnvelope() => new(
        WebhookEventId: "evt-1",
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

    [Fact]
    public async Task EnqueueAsync_PersistsEntryWithRoundTrippablePayload()
    {
        var envelope = SampleEnvelope();
        using var scope = _provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        await writer.EnqueueAsync(envelope, CancellationToken.None);

        using var readScope = _provider.CreateScope();
        var dbContext = readScope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        var entry = Assert.Single(dbContext.Entries);
        Assert.Equal("evt-1", entry.WebhookEventId);
        Assert.Equal(0, entry.Attempts);
        Assert.Null(entry.NextAttemptAt);

        var roundTripped = JsonSerializer.Deserialize<IngestEnvelope>(entry.PayloadJson);
        Assert.Equal(envelope, roundTripped);
    }

    [Fact]
    public async Task EnqueueAsync_NotifiesSignal()
    {
        using var scope = _provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        await writer.EnqueueAsync(SampleEnvelope(), CancellationToken.None);

        Assert.Equal(1, _signal.NotifyCount);
    }
}
