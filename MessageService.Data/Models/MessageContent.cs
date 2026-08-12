namespace MessageService.Models;

public class MessageContent
{
    public long Id { get; set; }
    public long GroupMessageId { get; set; }
    public GroupMessage? GroupMessage { get; set; }

    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public DownloadStatus DownloadStatus { get; set; }
    public byte[]? Content { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>累計標記為 Failed 的次數；達上限（見 ContentDownloadOptions.MaxFailedRetries）後
    /// 不再被 RequeuePendingAsync 撿回，避免 LINE 內容過期後永遠重跑。</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}
