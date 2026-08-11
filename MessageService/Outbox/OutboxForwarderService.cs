using System.Text.Json;
using MessageService.Options;
using MessageService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Outbox;

/// <summary>把 outbox 排空、經 IIngestSink 落地。寫入 outbox 會立刻叫醒這裡（見 IOutboxSignal），
/// 輪詢間隔只是保底，用來撿回退避到期的重試項目。</summary>
public class OutboxForwarderService(
    IServiceScopeFactory scopeFactory,
    IOutboxSignal signal,
    IOptions<OutboxOptions> options,
    ILogger<OutboxForwarderService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>處理一批到期的 outbox 項目。回傳是否處理了至少一筆——公開方法，
    /// 方便測試直接呼叫而不必跑計時迴圈（比照 ContentDownloadService 的既有測試慣例）。</summary>
    public async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        var sink = scope.ServiceProvider.GetRequiredService<IIngestSink>();

        var now = DateTimeOffset.UtcNow;
        var batch = await dbContext.Entries
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

                await sink.SubmitAsync(envelope, cancellationToken);

                dbContext.Entries.Remove(entry);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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
        var seconds = Math.Min(_options.BaseRetryDelaySeconds * attempts, _options.MaxRetryDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
