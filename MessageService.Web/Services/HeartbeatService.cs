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
    TimeProvider timeProvider,
    IOptions<HeartbeatOptions> options,
    IOptions<IngestOptions> ingestOptions,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private readonly HeartbeatOptions _options = options.Value;
    private readonly string _targetDescription = string.IsNullOrWhiteSpace(ingestOptions.Value.BaseUrl)
        ? "本機資料庫"
        : new Uri(HttpBaseAddress.Create(ingestOptions.Value.BaseUrl), "api/ingest/heartbeat").ToString();

    /// <summary>連續失敗的告警節流時點。單向防火牆拓撲（只開通 core→edge）下，Edge 送不到
    /// 心跳是**穩態**而不是異常——每個週期噴一次完整堆疊，一天就是上千筆雜訊，真正的問題
    /// 反而被埋掉。只在轉為失敗時記一次完整堆疊，持續期間每 10 分鐘記一則摘要。</summary>
    private DateTimeOffset? _lastFailureLogAt;

    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TryReportOnceAsync(stoppingToken);

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

    /// <summary>跑一次回報並套用失敗告警節流。回傳這次是否成功——公開方法讓測試不必跑
    /// 計時迴圈就能驗證節流行為（比照 OutboxForwarderService.ProcessBatchAsync 的慣例）。</summary>
    public async Task<bool> TryReportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReportOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 含 HttpClient 逾時的 TaskCanceledException：它不是停機，穿出去會讓 ExecuteAsync 結束、
            // BackgroundService 預設 StopHost 把整個站台停掉（判斷依據看 token，不看例外型別）
            LogFailure(ex);
            return false;
        }

        if (_failing)
        {
            _failing = false;
            _lastFailureLogAt = null;
            logger.LogInformation("心跳回報已恢復正常。");
        }
        return true;
    }

    private void LogFailure(Exception ex)
    {
        var now = timeProvider.GetUtcNow();
        if (!_failing)
        {
            _failing = true;
            _lastFailureLogAt = now;
            logger.LogWarning(ex,
                "Failed to report heartbeat（目標 {Target}）：{FailureReason}；持續失敗期間這則告警每 10 分鐘最多再記一次。",
                _targetDescription,
                OutboundFailureClassifier.Classify(ex));
            return;
        }

        if (_lastFailureLogAt is { } last && now - last >= TimeSpan.FromMinutes(10))
        {
            _lastFailureLogAt = now;
            logger.LogWarning("Failed to report heartbeat（目標 {Target}，仍然失敗）：{FailureReason}",
                _targetDescription,
                OutboundFailureClassifier.Classify(ex));
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
