namespace MessageService.Services;

/// <summary>POST /api/ingest/events 的回應 body。跟 IngestResult 形狀一樣，故意不共用同一個
/// 型別——IngestResult 是 IIngestSink 的內部契約，這個是外部 HTTP 介面的序列化格式，
/// 讓兩者的變動理由保持獨立（HTTP 回應格式要顧及相容性，內部型別不必）。</summary>
public record IngestEventResponse(long? ContentId);
