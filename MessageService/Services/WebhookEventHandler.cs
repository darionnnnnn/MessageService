using MessageService.Data;
using MessageService.Models;
using MessageService.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

public class WebhookEventHandler(
    MessageDbContext dbContext,
    IContentDownloadQueue downloadQueue,
    ILogger<WebhookEventHandler> logger) : IWebhookEventHandler
{
    private static readonly HashSet<string> DownloadableTypes = ["image", "video", "file"];
    private static readonly HashSet<string> SupportedTypes = ["text", "sticker", "image", "video", "file"];

    public async Task HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        foreach (var webhookEvent in request.Events)
        {
            await HandleEventAsync(webhookEvent, cancellationToken);
        }
    }

    private async Task HandleEventAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        if (webhookEvent.Type != "message"
            || webhookEvent.Source?.Type != "group"
            || webhookEvent.Source.GroupId is null
            || webhookEvent.Message is null
            || webhookEvent.WebhookEventId is null
            || webhookEvent.Message.Type is not { } messageType
            || !SupportedTypes.Contains(messageType))
        {
            return;
        }

        var alreadyExists = await dbContext.GroupMessages
            .AnyAsync(m => m.WebhookEventId == webhookEvent.WebhookEventId, cancellationToken);
        if (alreadyExists)
        {
            logger.LogInformation("Skipping duplicate webhook event {WebhookEventId}", webhookEvent.WebhookEventId);
            return;
        }

        var text = messageType switch
        {
            "text" => webhookEvent.Message.Text,
            "sticker" => "(貼圖)",
            _ => null
        };

        var groupMessage = new GroupMessage
        {
            WebhookEventId = webhookEvent.WebhookEventId,
            LineMessageId = webhookEvent.Message.Id ?? "",
            GroupId = webhookEvent.Source.GroupId,
            UserId = webhookEvent.Source.UserId,
            MessageType = messageType,
            Text = text,
            EventTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(webhookEvent.Timestamp),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        if (DownloadableTypes.Contains(messageType))
        {
            groupMessage.Content = new MessageContent
            {
                FileName = messageType == "file" ? webhookEvent.Message.FileName : null,
                DownloadStatus = DownloadStatus.Pending
            };
        }

        dbContext.GroupMessages.Add(groupMessage);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Failed to save webhook event {WebhookEventId}, treating as duplicate", webhookEvent.WebhookEventId);
            return;
        }

        logger.LogInformation("Saved {MessageType} message {LineMessageId} from group {GroupId}{Pending}",
            messageType, groupMessage.LineMessageId, groupMessage.GroupId,
            groupMessage.Content is null ? "" : " (content download queued)");

        if (groupMessage.Content is not null)
        {
            downloadQueue.Enqueue(groupMessage.Content.Id);
        }
    }
}
