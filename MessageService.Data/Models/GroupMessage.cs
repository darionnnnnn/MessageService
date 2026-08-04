namespace MessageService.Models;

public class GroupMessage
{
    public long Id { get; set; }
    public required string WebhookEventId { get; set; }
    public required string LineMessageId { get; set; }
    public required string GroupId { get; set; }
    public string? UserId { get; set; }
    public required string MessageType { get; set; }
    public string? Text { get; set; }

    /// <summary>MessageType=sticker 時的貼圖識別碼；改版前收到的貼圖沒有這兩個欄位（LINE 不提供舊訊息回溯查詢），
    /// 前端顯示時要對 null 做 fallback。</summary>
    public string? StickerId { get; set; }
    public string? PackageId { get; set; }

    public DateTimeOffset EventTimestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public MessageContent? Content { get; set; }
}
