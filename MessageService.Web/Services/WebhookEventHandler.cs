using MessageService.Models.Line;
using MessageService.Outbox;

namespace MessageService.Services;

/// <summary>只負責把 webhook 事件解析、過濾成 IngestEnvelope，寫進本地 outbox 就回——
/// 完全不碰資料庫、不打任何網路，webhook 回應時間因此跟後端（不論是直連的資料庫，
/// 還是 Line 模式要打的遠端 API）是否可用完全脫鉤。真正的落地邏輯在 outbox 排空後
/// 交給 IIngestSink（DirectIngestSink／未來的 HttpIngestSink）。</summary>
public class WebhookEventHandler(
    IOutboxWriter outboxWriter,
    ILogger<WebhookEventHandler> logger) : IWebhookEventHandler
{
    private static readonly HashSet<string> DownloadableTypes = ["image", "video", "audio", "file", "sticker"];
    private static readonly HashSet<string> SupportedTypes = ["text", "sticker", "image", "video", "audio", "file"];

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

        var text = messageType switch
        {
            "text" => webhookEvent.Message.Text,
            "sticker" => "(貼圖)",
            _ => null
        };

        var envelope = new IngestEnvelope(
            WebhookEventId: webhookEvent.WebhookEventId,
            LineMessageId: webhookEvent.Message.Id ?? "",
            GroupId: webhookEvent.Source.GroupId,
            UserId: webhookEvent.Source.UserId,
            MessageType: messageType,
            Text: text,
            // Text 維持 "(貼圖)" 當 fallback 顯示用（前端載圖失敗或舊訊息沒有這兩個欄位時）
            StickerId: messageType == "sticker" ? webhookEvent.Message.StickerId : null,
            PackageId: messageType == "sticker" ? webhookEvent.Message.PackageId : null,
            EventTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(webhookEvent.Timestamp),
            ReceivedAt: DateTimeOffset.UtcNow,
            HasContent: DownloadableTypes.Contains(messageType),
            ContentFileName: messageType == "file" ? webhookEvent.Message.FileName : null);

        await outboxWriter.EnqueueAsync(envelope, cancellationToken);

        logger.LogInformation("Queued {MessageType} message {LineMessageId} from group {GroupId} to outbox",
            messageType, envelope.LineMessageId, envelope.GroupId);
    }
}
