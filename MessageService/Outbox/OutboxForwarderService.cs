using System.Text.Json;
using MessageService.Options;
using MessageService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Outbox;

/// <summary>把 outbox 排空、經 IIngestSink 落地。寫入 outbox 會立刻叫醒這裡（見 IOutboxSignal），
/// 輪詢間隔只是保底，用來撿回退避到期的重試項目。落地成功後用 IngestSideEffects 決定這台
/// 主機要不要接手媒體下載／頭貼刷新——downloadQueue／profileRefreshQueue 是單例、
/// 依 Line:OutboundHere 在 DI 註冊時決定是真 Channel 還是 Null 實作，直接建構子注入即可，
/// 不必比照 sink 走per-batch scope。</summary>
public class OutboxForwarderService(
    IServiceScopeFactory scopeFactory,
    IOutboxSignal signal,
    IContentDownloadQueue downloadQueue,
    IProfileRefreshQueue profileRefreshQueue,
    IOptions<OutboxOptions> options,
    ILogger<OutboxForwarderService> logger) : BackgroundService
{
    private static readonly TimeSpan DeadLetterCheckInterval = TimeSpan.FromHours(1);

    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動時先報一次（比照舊的 Program.cs 啟動 log），之後每小時再報一次——死信不會自動
        // 消失，只會在這裡的 log 被看到，沒有專用的重送介面，量大時要靠這行提醒維運人員去查
        var nextDeadLetterCheck = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedAny = false;
            try
            {
                processedAny = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error while forwarding outbox entries");
            }

            if (DateTimeOffset.UtcNow >= nextDeadLetterCheck)
            {
                try
                {
                    await LogDeadLetterCountAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to check outbox dead-letter count");
                }
                nextDeadLetterCheck = DateTimeOffset.UtcNow + DeadLetterCheckInterval;
            }

            // 這輪有處理到東西時，outbox 可能還有剩（一次最多 BatchSize 筆），
            // 立刻再跑一輪撿剩下的，不必等門鈴或輪詢間隔
            if (!processedAny)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 停機中，讓 while 迴圈條件收尾
                }
            }
        }
    }

    public async Task LogDeadLetterCountAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        var deadLetterCount = await dbContext.Entries.CountAsync(e => e.DeadLetteredAt != null, cancellationToken);
        if (deadLetterCount > 0)
        {
            logger.LogWarning(
                "Outbox has {Count} dead-lettered entries awaiting manual review (see LastError column in outbox.db)",
                deadLetterCount);
        }
    }

    /// <summary>處理一批到期的 outbox 項目。回傳是否處理了至少一筆——公開方法，
    /// 方便測試直接呼叫而不必跑計時迴圈（比照 ContentDownloadService 的既有測試慣例）。</summary>
    public async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        var sink = scope.ServiceProvider.GetRequiredService<IIngestSink>();

        var now = DateTimeOffset.UtcNow;
        var batch = await dbContext.Entries
            .Where(e => e.DeadLetteredAt == null)
            .Where(e => e.NextAttemptAt == null || e.NextAttemptAt <= now)
            .OrderBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return false;
        }

        foreach (var entry in batch)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<IngestEnvelope>(entry.PayloadJson)
                    ?? throw new InvalidOperationException("Outbox entry payload deserialized to null.");

                var result = await sink.SubmitAsync(envelope, cancellationToken);
                // 這台主機（不是落地端那台）要不要接手媒體下載／頭貼刷新，見 IngestSideEffects
                // 說明——在 Full 模式下 sink 是 DirectIngestSink，落地跟這裡是同一台主機；
                // Line 模式下 sink 是 HttpIngestSink，ContentId 是從遠端 ingest API 的回應帶回來的
                IngestSideEffects.Apply(envelope, result, downloadQueue, profileRefreshQueue);

                dbContext.Entries.Remove(entry);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (PermanentIngestException ex)
            {
                // 落地端明確判定「重試也沒用」（例如 ingest API 回 400）——不管累計次數，
                // 第一次遇到就直接死信，不浪費重試次數也不刷無意義的退避 log
                entry.Attempts++;
                entry.LastError = ex.Message;
                entry.DeadLetteredAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogError(ex,
                    "Outbox entry {Id} (WebhookEventId {WebhookEventId}) permanently rejected by sink, dead-lettering without retry",
                    entry.Id, entry.WebhookEventId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 暫時性失敗永遠重試、不死信——短暫斷線與長時間停機都不該讓事件遺失，
                // 見 OutboxOptions 的說明
                entry.Attempts++;
                entry.LastError = ex.Message;
                entry.NextAttemptAt = now + ComputeBackoff(entry.Attempts);
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogWarning(ex,
                    "Failed to forward outbox entry {Id} (WebhookEventId {WebhookEventId}), attempt {Attempts}, retrying at {NextAttemptAt}",
                    entry.Id, entry.WebhookEventId, entry.Attempts, entry.NextAttemptAt);
            }
        }

        return true;
    }

    private TimeSpan ComputeBackoff(int attempts)
    {
        // 指數退避：第 N 次失敗延遲 BaseRetryDelaySeconds × 2^(N-1)，封頂 MaxRetryDelaySeconds。
        // attempts 很大時 Math.Pow 會趨近 double.PositiveInfinity，Math.Min 仍會正確封頂，
        // 不需要額外的溢位保護。
        var exponentialSeconds = _options.BaseRetryDelaySeconds * Math.Pow(2, attempts - 1);
        var seconds = Math.Min(exponentialSeconds, _options.MaxRetryDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
