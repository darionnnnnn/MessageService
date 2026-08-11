using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：直接寫進本機可連到的資料庫。這是原本
/// WebhookEventHandler.HandleEventAsync 落地那一半邏輯搬過來的結果，行為刻意保持一致——
/// 差別只在輸入從 WebhookEvent 換成已經解析過的 IngestEnvelope。</summary>
public class DirectIngestSink(
    MessageDbContext dbContext,
    IContentDownloadQueue downloadQueue,
    IProfileRefreshQueue profileRefreshQueue,
    ILogger<DirectIngestSink> logger) : IIngestSink
{
    public async Task SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        // 防重送的真正保證是 GroupMessages.WebhookEventId 的唯一索引（見 MessageDbContext）
        // 加下面的 DbUpdateException 攔截；這句預查只是省一次白跑的 INSERT，不是正確性來源——
        // outbox 重試、或 Line 模式重送同一個 WebhookEventId 時，沒有這句預查也不會產生重複資料
        var alreadyExists = await dbContext.GroupMessages
            .AnyAsync(m => m.WebhookEventId == envelope.WebhookEventId, cancellationToken);
        if (alreadyExists)
        {
            logger.LogInformation("Skipping duplicate webhook event {WebhookEventId}", envelope.WebhookEventId);
            return;
        }

        var groupMessage = new GroupMessage
        {
            WebhookEventId = envelope.WebhookEventId,
            LineMessageId = envelope.LineMessageId,
            GroupId = envelope.GroupId,
            UserId = envelope.UserId,
            MessageType = envelope.MessageType,
            Text = envelope.Text,
            StickerId = envelope.StickerId,
            PackageId = envelope.PackageId,
            EventTimestamp = envelope.EventTimestamp,
            ReceivedAt = envelope.ReceivedAt
        };

        if (envelope.HasContent)
        {
            groupMessage.Content = new MessageContent
            {
                FileName = envelope.ContentFileName,
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
            // 唯一索引撞鍵（真重複）與暫時性儲存失敗（斷線、逾時）都會走到這裡，但處置完全相反：
            // 重複＝成功（讓 outbox 刪掉該筆）、暫時性失敗＝往外拋（讓 outbox 重試）。
            // 不解析各 provider 的錯誤碼，直接回查資料庫確認這筆到底有沒有進去。
            // 回查前先把失敗的實體從 change tracker 清掉——forwarder 一個批次共用同一個 scope
            // 的 DbContext，殘留的 Added 實體會污染同批後續每一筆的 SaveChanges
            dbContext.ChangeTracker.Clear();

            var saved = await dbContext.GroupMessages
                .AnyAsync(m => m.WebhookEventId == envelope.WebhookEventId, cancellationToken);
            if (saved)
            {
                // 資料在（撞鍵、或前一次嘗試其實已 commit 才斷線）→ 當重複處理。
                // 後者若有 Pending 內容，這次不會補入列，由下次服務重啟的啟動接續撈回
                logger.LogInformation(ex, "Webhook event {WebhookEventId} already persisted, treating as duplicate", envelope.WebhookEventId);
                return;
            }

            logger.LogWarning(ex, "Transient failure saving webhook event {WebhookEventId}, leaving in outbox for retry", envelope.WebhookEventId);
            throw;
        }

        logger.LogInformation("Saved {MessageType} message {LineMessageId} from group {GroupId}{Pending}",
            envelope.MessageType, groupMessage.LineMessageId, groupMessage.GroupId,
            groupMessage.Content is null ? "" : " (content download queued)");

        if (groupMessage.Content is not null)
        {
            downloadQueue.Enqueue(groupMessage.Content.Id);
        }

        profileRefreshQueue.Enqueue(new ProfileRefreshTask(groupMessage.GroupId, groupMessage.UserId));
    }
}
