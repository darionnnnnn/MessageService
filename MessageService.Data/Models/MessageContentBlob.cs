namespace MessageService.Models;

public class MessageContentBlob
{
    public long MessageContentId { get; set; }
    public required byte[] Content { get; set; }

    public MessageContent? MessageContent { get; set; }
}
