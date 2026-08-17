namespace MessageService.Models;

public class MessageContent
{
    public long Id { get; set; }
    public long GroupMessageId { get; set; }
    public GroupMessage? GroupMessage { get; set; }

    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public DownloadStatus DownloadStatus { get; set; }
    public MessageContentBlob? Blob { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>這筆內容被某個站台認領去下載的 UTC 時間（租約）。
    /// null 表示尚未被任何站台認領；有值表示已認領但尚未完成，逾期後（超過租約期限）才可被
    /// 其他站台重新認領，避免重複下載同一筆內容。例外是啟動掃描：認領人是自己這個站台時
    /// 不看租約也會回收（那是上次行程留下的孤兒），詳見 DbContentWorkSource.GetPendingIdsAsync。</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>這筆內容目前由哪一個站台持有認領（機器名＋站台目錄雜湊，見 ProcessOwnerId）。
    /// 釋放（完成、失敗、回退、回收）時清為 null。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>累計標記為 Failed 的次數；達上限（見 ContentDownloadOptions.MaxFailedRetries）後
    /// 不再被 RequeuePendingAsync 撿回，避免 LINE 內容過期後永遠重跑。</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}
