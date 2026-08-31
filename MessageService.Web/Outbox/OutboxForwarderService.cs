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
    EdgeChannelState channelState,
    IOptions<OutboxOptions> options,
    IOptions<HeartbeatOptions> heartbeatOptions,
    ILogger<OutboxForwarderService> logger) : BackgroundService
{
    private static readonly TimeSpan DeadLetterCheckInterval = TimeSpan.FromHours(1);

    private readonly OutboxOptions _options = options.Value;
    private readonly HeartbeatOptions _heartbeatOptions = heartbeatOptions.Value;

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

        // P0 那類「批次 ingest 撞重複鍵 500，Edge outbox 永久卡死但完全沒有告警」的情況——
        // 死信計數看不出來（暫時性失敗永遠不會死信，見 OutboxEntry.DeadLetteredAt 說明），
        // 只能靠最舊未死信項目的年齡判斷排空是不是卡住了。順著同一個每小時迴圈檢查，
        // 第一個小時內就會被叫出來，不用等到有人發現「怎麼今天都沒新訊息」
        var oldestPendingCreatedAt = await dbContext.Entries
            .WhereDeliverable()
            .OrderBy(e => e.CreatedAt)
            .Select(e => (DateTimeOffset?)e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (oldestPendingCreatedAt is { } createdAt)
        {
            var age = DateTimeOffset.UtcNow - createdAt;
            var alertThreshold = TimeSpan.FromMinutes(_heartbeatOptions.OutboxBacklogAlertMinutes);
            if (age >= alertThreshold)
            {
                logger.LogError(
                    "Outbox has a pending entry that has been waiting {AgeMinutes:F0} minutes " +
                    "(alert threshold {ThresholdMinutes} minutes) — forwarding may be stuck",
                    age.TotalMinutes, _heartbeatOptions.OutboxBacklogAlertMinutes);
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
            .WherePushDue(now)
            .OrderBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return false;
        }

        // edge→core 這個方向不通時（Auto 模式推送失敗後）不空試——Core 端會改用輪詢把
        // 資料取走，這裡只要每隔一個探測週期放行一次，通了就自動恢復推送。見 EdgeChannelState。
        // 閘門放在取完 batch 之後：空批次送不出任何東西，拿它當探測會讓計時白白重置
        if (!channelState.ShouldAttemptPush())
        {
            return false;
        }

        // 反序列化本身失敗（不該發生，但防禦性處理）不算落地失敗——payload 壞了重試也不會變好，
        // 直接死信，不讓這種項目卡進下面的批次呼叫。
        // 這裡用索引子賦值而非 group：OutboxDbContext 對 WebhookEventId 有唯一索引，同一批 batch
        // 裡不可能出現兩列相同的 WebhookEventId（DB 層面保證），不需要為理論上不會發生的重複
        // 多寫一層 group 邏輯——見 SqliteOutboxWriter.EnqueueAsync 對撞鍵的處理
        var entriesByWebhookEventId = new Dictionary<string, OutboxEntry>();
        var envelopesByWebhookEventId = new Dictionary<string, IngestEnvelope>();
        var envelopes = new List<IngestEnvelope>();
        foreach (var entry in batch)
        {
            IngestEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<IngestEnvelope>(entry.PayloadJson)
                    ?? throw new InvalidOperationException("Outbox entry payload deserialized to null.");
            }
            catch (Exception ex)
            {
                entry.Attempts++;
                entry.LastError = ex.Message;
                entry.DeadLetteredAt = now;
                logger.LogError(ex,
                    "Outbox entry {Id} (WebhookEventId {WebhookEventId}) has an unreadable payload, dead-lettering without retry",
                    entry.Id, entry.WebhookEventId);
                continue;
            }

            entriesByWebhookEventId[entry.WebhookEventId] = entry;
            envelopesByWebhookEventId[entry.WebhookEventId] = envelope;
            envelopes.Add(envelope);
        }

        if (envelopes.Count == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        // 問題9：一次 HTTP 請求送整批，取代逐筆各自一次 RTT——見 IIngestSink.SubmitBatchAsync
        // 說明。DirectIngestSink（AllInOne／Core）用介面預設實作（順序處理、單筆
        // PermanentIngestException 只影響那一筆）；HttpIngestSink（Edge）真的一次送整批。
        IReadOnlyList<IngestBatchItemResult>? results = null;
        Exception? batchFailure = null;
        try
        {
            results = await sink.SubmitBatchAsync(envelopes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            batchFailure = ex;
        }

        if (results is not null)
        {
            var mentioned = new HashSet<string>();
            foreach (var item in results)
            {
                mentioned.Add(item.WebhookEventId);
                if (!entriesByWebhookEventId.TryGetValue(item.WebhookEventId, out var entry))
                {
                    continue;
                }

                if (item.PermanentlyRejected)
                {
                    // 落地端明確判定「重試也沒用」（例如 ingest API 回 400）——不管累計次數，
                    // 第一次遇到就直接死信，不浪費重試次數也不刷無意義的退避 log
                    entry.Attempts++;
                    entry.LastError = item.Error;
                    entry.DeadLetteredAt = now;
                    logger.LogError(
                        "Outbox entry {Id} (WebhookEventId {WebhookEventId}) permanently rejected by sink, dead-lettering without retry: {Error}",
                        entry.Id, entry.WebhookEventId, item.Error);
                    continue;
                }

                // 這台主機（不是落地端那台）要不要接手媒體下載／頭貼刷新，見 IngestSideEffects
                // 說明——在 AllInOne 模式下 sink 是 DirectIngestSink，落地跟這裡是同一台主機；
                // Edge 模式下 sink 是 HttpIngestSink，ContentId 是從遠端 ingest API 的回應帶回來的
                var envelope = envelopesByWebhookEventId[item.WebhookEventId];
                IngestSideEffects.Apply(envelope, new IngestResult(item.ContentId), downloadQueue, profileRefreshQueue);

                dbContext.Entries.Remove(entry);
            }

            // 這批真的送到了對端（不論個別項目是成功還是被永久拒絕）——推送方向是通的
            channelState.MarkPushSucceeded();

            // 批次結果沒提到的項目：現有兩套實作都會完整回覆整批（暫時性失敗是整批往外拋，
            // 不會只回部分結果），走到這裡代表對端行為異常——照暫時性失敗給退避，不能
            // 「原樣不動」：NextAttemptAt 沒推進的話這批會立刻重跑，變成無退避的熱迴圈。
            // 重試對已處理過的項目安全（IIngestSink 的冪等保證）
            foreach (var entry in entriesByWebhookEventId.Values.Where(e => !mentioned.Contains(e.WebhookEventId)))
            {
                entry.Attempts++;
                entry.LastError = "Batch response did not mention this entry";
                entry.NextAttemptAt = now + ComputeBackoff(entry.Attempts);
                logger.LogWarning(
                    "Outbox entry {Id} (WebhookEventId {WebhookEventId}) was not mentioned in the batch response, retrying at backoff",
                    entry.Id, entry.WebhookEventId);
            }
        }
        else
        {
            // 整批呼叫本身失敗（例如連不上 ingest API）——所有項目都當暫時性失敗處理，
            // 沿用既有的指數退避；短暫斷線與長時間停機都不該讓事件遺失，見 OutboxOptions 的說明
            foreach (var entry in entriesByWebhookEventId.Values)
            {
                entry.Attempts++;
                entry.LastError = batchFailure!.Message;
                entry.NextAttemptAt = now + ComputeBackoff(entry.Attempts);
            }

            // 整批連不上對端：在 Auto 模式下暫停推送，改由 Core 端輪詢接手，
            // 之後每隔一個探測週期再試一次（見 EdgeChannelState）
            var wasPaused = channelState.PushPaused;
            channelState.MarkPushFailed();
            if (!wasPaused && channelState.PushPaused)
            {
                logger.LogWarning(batchFailure,
                    "推送到 Core 失敗，暫停主動推送改由對方輪詢接手，之後每隔一個探測週期再試一次。");
            }

            logger.LogWarning(batchFailure,
                "Failed to forward outbox batch of {Count} entries, retrying at backoff", entriesByWebhookEventId.Count);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

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
