using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

// 需求4：心跳要能算出 outbox 積壓給檢視端看，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次D。
// 只有收 webhook 的主機才有 outbox 可算，其餘主機一律回報 null——這裡直接測
// HeartbeatService.ReportOnceAsync，不透過計時迴圈。
public class HeartbeatServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeHeartbeatReporter _reporter = new();

    public HeartbeatServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IHeartbeatReporter>(_ => _reporter);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private HeartbeatService CreateService(bool receivesWebhook) =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new DeploymentCapabilities(
                ReceivesWebhook: receivesWebhook, HasDatabaseAccess: true, IngestApiEnabled: false,
                ViewerEnabled: true, OutboundHere: false, RunsRetention: false, EdgePullApiEnabled: false),
            TimeProvider.System,
            OptionsFactory.Create(new HeartbeatOptions()),
            NullLogger<HeartbeatService>.Instance);

    private async Task SeedOutboxEntryAsync(DateTimeOffset createdAt, DateTimeOffset? deadLetteredAt = null)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        dbContext.Entries.Add(new OutboxEntry
        {
            WebhookEventId = Guid.NewGuid().ToString(),
            PayloadJson = "{}",
            CreatedAt = createdAt,
            DeadLetteredAt = deadLetteredAt
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task ReportOnceAsync_NotReceivingWebhook_ReportsNullOutboxStats()
    {
        var service = CreateService(receivesWebhook: false);

        await service.ReportOnceAsync(CancellationToken.None);

        var report = Assert.Single(_reporter.Reported);
        Assert.Null(report.OutboxPending);
        Assert.Null(report.OutboxOldestAgeSeconds);
    }

    [Fact]
    public async Task ReportOnceAsync_ReceivesWebhook_EmptyOutbox_ReportsZeroPendingAndNullAge()
    {
        var service = CreateService(receivesWebhook: true);

        await service.ReportOnceAsync(CancellationToken.None);

        var report = Assert.Single(_reporter.Reported);
        Assert.Equal(0, report.OutboxPending);
        Assert.Null(report.OutboxOldestAgeSeconds);
    }

    [Fact]
    public async Task ReportOnceAsync_ReceivesWebhook_ComputesPendingCountAndOldestAge()
    {
        await SeedOutboxEntryAsync(DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedOutboxEntryAsync(DateTimeOffset.UtcNow.AddMinutes(-2));
        var service = CreateService(receivesWebhook: true);

        await service.ReportOnceAsync(CancellationToken.None);

        var report = Assert.Single(_reporter.Reported);
        Assert.Equal(2, report.OutboxPending);
        Assert.NotNull(report.OutboxOldestAgeSeconds);
        Assert.True(report.OutboxOldestAgeSeconds >= 590); // ~10 分鐘，留一點誤差空間
    }

    [Fact]
    public async Task ReportOnceAsync_DeadLetteredEntriesExcludedFromPendingCount()
    {
        await SeedOutboxEntryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedOutboxEntryAsync(DateTimeOffset.UtcNow.AddDays(-1), deadLetteredAt: DateTimeOffset.UtcNow);
        var service = CreateService(receivesWebhook: true);

        await service.ReportOnceAsync(CancellationToken.None);

        var report = Assert.Single(_reporter.Reported);
        Assert.Equal(1, report.OutboxPending); // 死信那筆不計入積壓
    }
}
