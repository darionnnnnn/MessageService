using MessageService.Options;
using MessageService.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>所有部署模式都跑（見 Program.cs）——需求4：Web 端要能看到另外幾台服務是否正常
/// 運作。定期把自己的存活狀態寫進 HostHeartbeats（見 IHeartbeatReporter 的兩種實作），
/// 收 webhook 的主機（AllInOne／Edge）順便算好 outbox 積壓數與最舊項目年齡一起回報。
/// 心跳本身不是關鍵路徑，失敗只記警告，不影響其他背景服務、也不重試。</summary>
public class HeartbeatService(
    IServiceScopeFactory scopeFactory,
    DeploymentCapabilities capabilities,
    IOptions<HeartbeatOptions> options,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private readonly HeartbeatOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReportOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to report heartbeat");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // 正常停機
            }
        }
    }

    public async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reporter = scope.ServiceProvider.GetRequiredService<IHeartbeatReporter>();

        var report = capabilities.ReceivesWebhook
            ? await OutboxStatsReader.ComputeAsync(scope.ServiceProvider.GetRequiredService<OutboxDbContext>(), cancellationToken)
            : new HeartbeatReport(null, null);

        await reporter.ReportAsync(report, cancellationToken);
    }
}
