using System.Net.Http.Json;
using System.Text.Json;
using MessageService.Controllers;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Core 端的反向通道：防火牆只開通 core→edge 時，由這台主動去 Edge 拉訊息與心跳。
///
/// 啟停由「最後一次收到<b>推送</b>心跳的時間」決定（見 <see cref="PushHeartbeatTracker"/>）——
/// 超過 Ingest:PullActivationSeconds 沒收到就開始輪詢，推送恢復就停止。這裡刻意不看
/// HostHeartbeats 表：輪詢自己拉回來的心跳也會寫進那張表，用表當判斷來源會讓輪詢把自己停掉。
///
/// 只在設定了 Ingest:EdgeBaseUrl 時註冊（見服務註冊矩陣）——沒設就完全不存在，
/// 行為與沒有這個功能時一致。</summary>
public class EdgePullService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    PushHeartbeatTracker pushHeartbeatTracker,
    TimeProvider timeProvider,
    IOptions<IngestOptions> ingestOptions,
    ILogger<EdgePullService> logger) : BackgroundService
{
    /// <summary>具名 HttpClient：短逾時，只用於 poll／ack 這種小 JSON 往返。
    /// blob 取回（作業C）另有長逾時的 client，兩者不可共用。</summary>
    public const string HttpClientName = "edge-pull";

    private readonly IngestOptions _options = ingestOptions.Value;

    /// <summary>連續失敗次數，決定退避倍率；成功歸零。</summary>
    private int _consecutiveFailures;

    /// <summary>目前是否處於「輪詢中」狀態，只用來讓狀態轉換各記一次 log。</summary>
    private bool _pulling;

    /// <summary>持續失敗時的摘要節流時點，避免 1 秒一次的失敗把 log 刷爆。</summary>
    private DateTimeOffset? _lastFailureSummaryAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // PollOnceAsync 內部已處理預期中的失敗；走到這裡代表落地或資料庫端出了非預期的錯，
                // 一樣要退避，不能讓迴圈以 1 秒節奏空轉重試
                RecordFailure(ex);
            }

            try
            {
                await Task.Delay(CurrentDelay(), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>判斷現在該不該輪詢：從未收過推送心跳（例如 edge→core 一開始就不通）也算該輪詢，
    /// 不必等門檻過去。</summary>
    public bool ShouldPull()
    {
        var lastPush = pushHeartbeatTracker.LastReceivedAt;
        if (lastPush is null)
        {
            return true;
        }

        return timeProvider.GetUtcNow() - lastPush.Value >= TimeSpan.FromSeconds(_options.PullActivationSeconds);
    }

    /// <summary>目前該等多久再跑下一輪：正常是 PullIntervalSeconds，連續失敗時指數退避到
    /// PullFailureMaxBackoffSeconds 為止。</summary>
    public TimeSpan CurrentDelay()
    {
        var baseSeconds = Math.Max(1, _options.PullIntervalSeconds);
        if (_consecutiveFailures == 0)
        {
            return TimeSpan.FromSeconds(baseSeconds);
        }

        var maxSeconds = Math.Max(baseSeconds, _options.PullFailureMaxBackoffSeconds);
        // 指數成長，但先夾住指數本身，避免連續失敗久了之後 Math.Pow 溢位
        var exponent = Math.Min(_consecutiveFailures - 1, 30);
        var seconds = baseSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }

    /// <summary>跑一次輪詢。回傳是否真的發出了 poll（推送通道還活著時回 false）。
    /// 公開方法讓測試不必跑計時迴圈就能驗證單次行為（比照 OutboxForwarderService.ProcessBatchAsync）。</summary>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!ShouldPull())
        {
            if (_pulling)
            {
                _pulling = false;
                logger.LogInformation(
                    "已收到 Edge 的推送心跳，停止輪詢 {EdgeBaseUrl}，改回由 Edge 主動推送。", _options.EdgeBaseUrl);
            }
            return false;
        }

        if (!_pulling)
        {
            _pulling = true;
            logger.LogInformation(
                "超過 {Seconds} 秒沒收到 Edge 的推送心跳，開始輪詢 {EdgeBaseUrl} 取回訊息與心跳。",
                _options.PullActivationSeconds, _options.EdgeBaseUrl);
        }

        EdgePollResponse? response;
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var pollResponse = await client.PostAsync("api/edge/poll", content: null, cancellationToken);
            pollResponse.EnsureSuccessStatusCode();
            response = await pollResponse.Content.ReadFromJsonAsync<EdgePollResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex);
            return true;
        }

        if (response is null)
        {
            RecordFailure(new InvalidOperationException("Edge 的 poll 回應無法反序列化。"));
            return true;
        }

        using var scope = scopeFactory.CreateScope();
        await WriteHeartbeatAsync(scope.ServiceProvider, response, cancellationToken);
        var acknowledged = await LandMessagesAsync(scope.ServiceProvider, response, cancellationToken);

        if (acknowledged.Count > 0)
        {
            try
            {
                using var ackResponse = await client.PostAsJsonAsync(
                    "api/edge/outbox/ack", new EdgeOutboxAckRequest(acknowledged), cancellationToken);
                ackResponse.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ack 沒送到不會掉資料：這批下次 poll 會再回來，落地端靠 WebhookEventId
                // 唯一索引去重。這裡照樣算一次失敗，讓退避生效
                RecordFailure(ex);
                return true;
            }
        }

        RecordSuccess();
        return true;
    }

    private async Task WriteHeartbeatAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (!HeartbeatIdentity.IsValid(response.Role, response.MachineName))
        {
            logger.LogWarning(
                "Edge 回報的心跳身分不合法（Role={Role}、MachineName 長度={Length}），略過這次心跳寫入。",
                response.Role, response.MachineName?.Length ?? 0);
            return;
        }

        var heartbeatStore = scopedProvider.GetRequiredService<IHeartbeatStore>();

        // 指紋固定 null：Edge 不碰加密金鑰，不能拿本機（Core）的指紋去填 Edge 那列——
        // 與 IngestController 代寫推送心跳的處置一致
        await heartbeatStore.UpsertAsync(
            response.Role, response.MachineName,
            new HeartbeatReport(response.OutboxPending, response.OutboxOldestAgeSeconds),
            encryptionKeyFingerprint: null, cancellationToken);
    }

    private async Task<List<string>> LandMessagesAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (response.Messages.Count == 0)
        {
            return [];
        }

        var envelopes = new List<IngestEnvelope>(response.Messages.Count);
        foreach (var item in response.Messages)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<IngestEnvelope>(item.PayloadJson)
                    ?? throw new InvalidOperationException("payload 反序列化為 null。");
                envelopes.Add(envelope);
            }
            catch (Exception ex)
            {
                // 壞掉的 payload 不 ack：留在 Edge 的 outbox 讓人工判斷，這裡重試也不會變好
                logger.LogError(ex,
                    "Edge 送來的 outbox 項目 {WebhookEventId} 無法反序列化，略過且不 ack。", item.WebhookEventId);
            }
        }

        if (envelopes.Count == 0)
        {
            return [];
        }

        var sink = scopedProvider.GetRequiredService<IIngestSink>();
        var downloadQueue = scopedProvider.GetRequiredService<IContentDownloadQueue>();
        var profileRefreshQueue = scopedProvider.GetRequiredService<IProfileRefreshQueue>();

        var results = await sink.SubmitBatchAsync(envelopes, cancellationToken);

        var envelopesByWebhookEventId = envelopes
            .GroupBy(e => e.WebhookEventId)
            .ToDictionary(g => g.Key, g => g.First());

        var acknowledged = new List<string>(results.Count);
        foreach (var item in results)
        {
            // 永久拒絕的也要 ack：Core 這邊已經判定重試不會變好，留在 Edge 只會無限重送
            if (!item.PermanentlyRejected && envelopesByWebhookEventId.TryGetValue(item.WebhookEventId, out var envelope))
            {
                IngestSideEffects.Apply(
                    envelope, new IngestResult(item.ContentId), downloadQueue, profileRefreshQueue);
            }

            acknowledged.Add(item.WebhookEventId);
        }

        return acknowledged;
    }

    private void RecordSuccess()
    {
        if (_consecutiveFailures > 0)
        {
            logger.LogInformation("輪詢 {EdgeBaseUrl} 已恢復正常。", _options.EdgeBaseUrl);
        }

        _consecutiveFailures = 0;
        _lastFailureSummaryAt = null;
    }

    private void RecordFailure(Exception ex)
    {
        _consecutiveFailures++;

        // 1 秒一次的失敗如果每次都記，log 一天會多出 8 萬行——只在「從正常轉為失敗」記一次，
        // 持續失敗期間每 10 分鐘補一則摘要
        var now = timeProvider.GetUtcNow();
        if (_consecutiveFailures == 1)
        {
            _lastFailureSummaryAt = now;
            logger.LogWarning(ex,
                "輪詢 {EdgeBaseUrl} 失敗，改為退避重試（上限 {MaxSeconds} 秒）。",
                _options.EdgeBaseUrl, _options.PullFailureMaxBackoffSeconds);
            return;
        }

        if (_lastFailureSummaryAt is { } last && now - last >= TimeSpan.FromMinutes(10))
        {
            _lastFailureSummaryAt = now;
            logger.LogWarning(
                "輪詢 {EdgeBaseUrl} 仍然失敗，已連續 {Count} 次。最後一次的原因：{Reason}",
                _options.EdgeBaseUrl, _consecutiveFailures, ex.Message);
        }
    }
}
