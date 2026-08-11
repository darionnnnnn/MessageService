namespace MessageService.Outbox;

/// <summary>webhook 收進來、尚未成功落地到後端的一筆事件。存在收錄端本機獨立的 SQLite 檔
/// （跟主資料庫完全分開，即使主資料庫／ingest API 打不通，這裡也還寫得進去）。</summary>
public class OutboxEntry
{
    public long Id { get; set; }

    /// <summary>只供 log 辨識用，不是去重鍵——真正的去重靠 IIngestSink 落地那端的資料庫唯一索引。</summary>
    public required string WebhookEventId { get; set; }

    /// <summary>序列化的 IngestEnvelope（System.Text.Json）。</summary>
    public required string PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>null＝隨時可處理；重試會把這個值往後推（見 OutboxOptions 的退避設定）。</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}
