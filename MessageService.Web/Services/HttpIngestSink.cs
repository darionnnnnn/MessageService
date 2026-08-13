using System.Net;
using System.Net.Http.Json;
using System.Threading;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Line 模式用：把 outbox 排出的事件推去 Db 模式主機的 ingest API，取代
/// DirectIngestSink 直連資料庫的角色（見 IIngestSink 的介面說明——這是唯一有兩套實作的
/// 落地方式，webhook 收進來的路徑本身不受影響）。
///
/// HTTP 狀態碼決定 forwarder 的處置：2xx 一律視為成功（IngestController 不區分「新寫入」
/// 與「重複」，兩者都回 200，跟 IIngestSink 既有的契約一致）；400 代表 payload 格式問題，
/// 重試不會變好，判定為永久失敗直接死信；其餘（含 401/403，可能是金鑰設定錯，
/// 修好設定後重試會成功）與所有 5xx／連線層錯誤一律當暫時性失敗，往外拋讓 outbox
/// 照退避排程重試。</summary>
public class HttpIngestSink(HttpClient httpClient, IOptions<IngestOptions> options, ILogger<HttpIngestSink> logger) : IIngestSink
{
    private const string HeaderName = "X-Ingest-Key";

    // 靜態旗標，跨這顆類別所有實例共用——AddHttpClient<TClient,TImplementation> 預設每次
    // 解析都給新實例，單一欄位存不住「已經印過警告」的狀態。刻意不是「已知 Core 不支援批次」
    // 的旗標：每次還是照樣先試批次端點，Core 升級後不用重啟 Edge 就會自動改用批次——只是
    // 避免升級前的過渡期每 5 秒（PollIntervalSeconds）洗一次警告 log。
    private static int _batchEndpointNotFoundWarned;

    public async Task<IngestResult> SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(envelope, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            // 回應 body 解析失敗（畸形 JSON）就讓例外往外拋，交給 outbox 照一般失敗流程重試——
            // 事件其實已經在對方落地了，重送一次不會產生重複（WebhookEventId 唯一索引撐著），
            // 下次會走到「重複」分支正常拿到 ContentId。這比刻意吞掉解析錯誤、把 ContentId
            // 留空等下次服務重啟才補回來得快，也不需要為了這個邊角案例另外處理
            var payload = await response.Content.ReadFromJsonAsync<IngestEventResponse>(cancellationToken);
            return new IngestResult(payload?.ContentId);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new PermanentIngestException(
                $"Ingest API rejected webhook event {envelope.WebhookEventId} as malformed (400): {body}");
        }

        throw new InvalidOperationException(
            $"Ingest API returned {(int)response.StatusCode} for webhook event {envelope.WebhookEventId}");
    }

    /// <summary>一次 HTTP 請求送整批，取代逐筆各自一次 RTT（見問題9）。Core 端還沒升級、
    /// 沒有這支端點時（404）退回逐筆模式，不讓拆機部署被迫同時升級兩端——升級順序建議
    /// 先升 Core 再升 Edge，見 docs/DEPLOYMENT-MODES.md。</summary>
    public async Task<IReadOnlyList<IngestBatchItemResult>> SubmitBatchAsync(
        IReadOnlyList<IngestEnvelope> envelopes, CancellationToken cancellationToken)
    {
        if (envelopes.Count == 0)
        {
            return [];
        }

        using var response = await SendBatchAsync(envelopes, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (Interlocked.CompareExchange(ref _batchEndpointNotFoundWarned, 1, 0) == 0)
            {
                logger.LogWarning(
                    "Core 端 ingest API 找不到批次端點 POST /api/ingest/events-batch（404）——" +
                    "可能還沒升級，暫時退回逐筆模式（{Count} 筆各自一次 HTTP round-trip）。" +
                    "升級順序請先升 Core 再升 Edge，見 docs/DEPLOYMENT-MODES.md。",
                    envelopes.Count);
            }
            return await SubmitOneByOneAsync(envelopes, cancellationToken);
        }

        if (response.IsSuccessStatusCode)
        {
            // body 是 null（200 但空回應，只可能是 Core 端有 bug）不能回空清單——空清單對
            // forwarder 代表「這批誰都沒被處理到」，項目原樣留著會立刻重跑、變成無退避的
            // 熱迴圈打爆 Core；往外拋讓 outbox 照退避重試才對
            var payload = await response.Content.ReadFromJsonAsync<List<IngestBatchItemResult>>(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Ingest API returned an empty batch response body for {envelopes.Count} events");
            return payload;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // 整包 400：ASP.NET Core 的模型驗證是對整個請求回 400，分不出是批次裡哪一筆有
            // 問題——若把整批擲成 PermanentIngestException，forwarder 的批次層級 catch 會把它
            // 當暫時性失敗無限退避重試（毒項目永不死信、還連坐同批健康項目）。退回逐筆模式
            // 隔離毒項目：有問題的那筆單獨拿到 400 → 永久拒絕死信，健康項目照常落地，
            // 恢復合併前逐筆版「單筆死信、其餘照走」的語意
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Ingest API 對整批請求回 400（{Body}）——退回逐筆模式隔離有問題的項目，{Count} 筆各自重送",
                body, envelopes.Count);
            return await SubmitOneByOneAsync(envelopes, cancellationToken);
        }

        throw new InvalidOperationException($"Ingest API returned {(int)response.StatusCode} for event batch of {envelopes.Count} events");
    }

    private async Task<IReadOnlyList<IngestBatchItemResult>> SubmitOneByOneAsync(
        IReadOnlyList<IngestEnvelope> envelopes, CancellationToken cancellationToken)
    {
        var results = new List<IngestBatchItemResult>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            try
            {
                var result = await SubmitAsync(envelope, cancellationToken);
                results.Add(new IngestBatchItemResult(envelope.WebhookEventId, result.ContentId, false, null));
            }
            catch (PermanentIngestException ex)
            {
                results.Add(new IngestBatchItemResult(envelope.WebhookEventId, null, true, ex.Message));
            }
        }
        return results;
    }

    private async Task<HttpResponseMessage> SendAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ingest/events")
        {
            Content = JsonContent.Create(envelope)
        };
        // API 金鑰是設定值、不是使用者輸入，不會出現 HttpHeaders 的合法字元檢查會刁難的內容，
        // 但仍用 TryAddWithoutValidation 避免萬一金鑰帶了不尋常字元讓整個請求直接炸掉
        request.Headers.TryAddWithoutValidation(HeaderName, options.Value.ApiKey ?? "");

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to reach ingest API for webhook event {envelope.WebhookEventId}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 逾時（不是呼叫端主動取消）——httpClient.Timeout 到期時 HttpClient 就是丟這個型別
            throw new InvalidOperationException(
                $"Ingest API request timed out for webhook event {envelope.WebhookEventId}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendBatchAsync(IReadOnlyList<IngestEnvelope> envelopes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ingest/events-batch")
        {
            Content = JsonContent.Create(envelopes)
        };
        request.Headers.TryAddWithoutValidation(HeaderName, options.Value.ApiKey ?? "");

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to reach ingest API for event batch of {envelopes.Count} events", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Ingest API batch request timed out for {envelopes.Count} events", ex);
        }
    }
}
