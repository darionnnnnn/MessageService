namespace MessageService.Outbox;

/// <summary>webhook 收進來、尚未成功落地到後端的一筆事件。存在收錄端本機獨立的 SQLite 檔
/// （跟主資料庫完全分開，即使主資料庫／ingest API 打不通，這裡也還寫得進去）。</summary>
public class OutboxEntry
{
    public long Id { get; set; }

    /// <summary>供 log 辨識用，也是這張表本身的去重鍵（唯一索引，見 OutboxDbContext）——
    /// LINE redelivery 送同一事件兩次時，outbox 只留一列；落地那端另有自己的資料庫唯一索引，
    /// 兩層去重互不依賴。</summary>
    public required string WebhookEventId { get; set; }

    /// <summary>序列化的 IngestEnvelope（System.Text.Json）。</summary>
    public required string PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>null＝隨時可處理；重試會把這個值往後推（見 OutboxOptions 的退避設定）。</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>非 null＝死信：落地端明確回報永久性失敗（PermanentIngestException，例如 payload
    /// 格式不合，重試不會改變結果）。**沒有累計次數門檻**——暫時性失敗一律指數退避無限重試，
    /// 不會因為試太多次就被放棄（那等於默默掉訊息）。死信項目不會被 forwarder 撿起，
    /// 但刻意不刪除這一列——訊息仍在，只是不再自動重試，需要人工判斷後手動處理。</summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
