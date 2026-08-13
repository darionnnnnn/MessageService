namespace MessageService.Services;

/// <summary>批次 ingest 單一項目的結果——同時是 IIngestSink.SubmitBatchAsync 的內部契約，
/// 也是 POST /api/ingest/events-batch 的 JSON 回應形狀（跟單筆端點的 IngestResult／
/// IngestEventResponse 刻意分兩型別不同，這裡兩者合一：批次端點是新功能，還沒有外部相容性
/// 包袱要顧）。
///
/// 沒被記錄在批次結果裡的項目（因為批次處理到一半遇到暫時性失敗而整批中止）代表「這次沒
/// 處理到」——呼叫端（OutboxForwarderService）維持該筆原樣，下次整批重試，對已經處理過的
/// 項目安全（IIngestSink 的冪等保證，見該介面說明）。</summary>
public record IngestBatchItemResult(string WebhookEventId, long? ContentId, bool PermanentlyRejected, string? Error);
