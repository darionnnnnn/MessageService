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
    public DateTimeOffset EventTimestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public MessageContent? Content { get; set; }
}
