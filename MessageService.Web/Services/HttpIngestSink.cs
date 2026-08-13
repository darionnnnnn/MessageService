using System.Net;
using System.Net.Http.Json;
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
public class HttpIngestSink(HttpClient httpClient, IOptions<IngestOptions> options) : IIngestSink
{
    private const string HeaderName = "X-Ingest-Key";

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
}
