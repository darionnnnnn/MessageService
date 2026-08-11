namespace MessageService.Services;

/// <summary>webhook 事件解析完成後的落地內容——outbox 序列化的 payload、
/// 未來 Stage 2 也會是 ingest API 的請求 body 格式，兩種用途共用同一個形狀。
/// 刻意不帶任何資料庫產生的值（Id 等），純粹是「要寫什麼」的描述。</summary>
public record IngestEnvelope(
    string WebhookEventId,
    string LineMessageId,
    string GroupId,
    string? UserId,
    string MessageType,
    string? Text,
    string? StickerId,
    string? PackageId,
    DateTimeOffset EventTimestamp,
    DateTimeOffset ReceivedAt,
    bool HasContent,
    string? ContentFileName);
