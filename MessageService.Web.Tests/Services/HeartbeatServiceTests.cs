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

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<HeartbeatService>
    {
        public int Warnings { get; private set; }
        public int Infos { get; private set; }
        public List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
                WarningMessages.Add(formatter(state, exception));
            }
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information) Infos++;
        }
    }

    private HeartbeatService CreateService(
        bool receivesWebhook,
        TimeProvider? timeProvider = null,
        Microsoft.Extensions.Options.IOptions<IngestOptions>? ingestOptions = null,
        Microsoft.Extensions.Logging.ILogger<HeartbeatService>? logger = null) =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new DeploymentCapabilities(
                ReceivesWebhook: receivesWebhook, HasDatabaseAccess: true, IngestApiEnabled: false,
                ViewerEnabled: true, OutboundHere: false, RunsRetention: false, EdgePullApiEnabled: false),
            timeProvider != null ? timeProvider : TimeProvider.System,
            OptionsFactory.Create(new HeartbeatOptions()),
            ingestOptions != null ? ingestOptions : OptionsFactory.Create(new IngestOptions()),
            logger != null ? logger : NullLogger<HeartbeatService>.Instance);

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

    [Fact]
    public async Task TryReportOnceAsync_ContinuousFailures_LogOncePlusTenMinuteSummaries()
    {
        var time = new FakeTimeProvider();
        var logger = new CountingLogger();
        var service = CreateService(receivesWebhook: false, timeProvider: time, logger: logger);
        _reporter.Failing = true;

        // 單向防火牆（只開通 core→edge）下心跳送不到是穩態——原本每個週期噴一次完整堆疊，
        // 一天上千筆雜訊；改成轉為失敗記一次、持續期間每 10 分鐘一則摘要
        for (var i = 0; i < 20; i++)
        {
            Assert.False(await service.TryReportOnceAsync(CancellationToken.None));
        }
        Assert.Equal(1, logger.Warnings);

        time.Now = time.Now.AddMinutes(11);
        await service.TryReportOnceAsync(CancellationToken.None);
        Assert.Equal(2, logger.Warnings);
    }

    [Fact]
    public async Task TryReportOnceAsync_Recovery_LogsOnceAndArmsFullLogAgain()
    {
        var time = new FakeTimeProvider();
        var logger = new CountingLogger();
        var service = CreateService(receivesWebhook: false, timeProvider: time, logger: logger);

        _reporter.Failing = true;
        await service.TryReportOnceAsync(CancellationToken.None);

        _reporter.Failing = false;
        Assert.True(await service.TryReportOnceAsync(CancellationToken.None));
        Assert.Equal(1, logger.Infos);

        // 恢復後再次失敗要重新記完整的一則，不能被舊的節流狀態吃掉
        _reporter.Failing = true;
        await service.TryReportOnceAsync(CancellationToken.None);
        Assert.Equal(2, logger.Warnings);
    }

    [Fact]
    public async Task TryReportOnceAsync_Failure_WithBaseUrl_LogsWarningWithTargetUrl()
    {
        var logger = new CountingLogger();
        var ingestOptions = OptionsFactory.Create(new IngestOptions { BaseUrl = "https://core-host.example" });
        var service = CreateService(receivesWebhook: false, ingestOptions: ingestOptions, logger: logger);
        _reporter.Failing = true;

        var result = await service.TryReportOnceAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, logger.Warnings);
        var msg = Assert.Single(logger.WarningMessages);
        Assert.Contains("https://core-host.example/api/ingest/heartbeat", msg);
    }

    /// <summary>HttpClient 逾時丟的是 TaskCanceledException（OperationCanceledException 的子類），
    /// 但 stoppingToken 沒被取消。若用例外型別過濾，它會穿出 TryReportOnceAsync、結束 ExecuteAsync，
    /// BackgroundService 預設 StopHost 把整個站台停掉。</summary>
    [Fact]
    public async Task TryReportOnceAsync_HttpClientTimeout_IsTreatedAsFailureNotShutdown()
    {
        var logger = new CountingLogger();
        var service = CreateService(receivesWebhook: false, logger: logger);
        _reporter.FailureToThrow = new TaskCanceledException("timeout", new TimeoutException());

        var result = await service.TryReportOnceAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public async Task TryReportOnceAsync_CallerCancelled_Propagates()
    {
        var service = CreateService(receivesWebhook: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _reporter.FailureToThrow = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TryReportOnceAsync(cts.Token));
    }

    [Fact]
    public async Task TryReportOnceAsync_Failure_WithoutBaseUrl_LogsWarningWithLocalDatabase()
    {
        var logger = new CountingLogger();
        var ingestOptions = OptionsFactory.Create(new IngestOptions { BaseUrl = null });
        var service = CreateService(receivesWebhook: false, ingestOptions: ingestOptions, logger: logger);
        _reporter.Failing = true;

        var result = await service.TryReportOnceAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, logger.Warnings);
        var msg = Assert.Single(logger.WarningMessages);
        Assert.Contains("本機資料庫", msg);
    }
}
