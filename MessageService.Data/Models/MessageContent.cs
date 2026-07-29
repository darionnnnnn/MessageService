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
}
