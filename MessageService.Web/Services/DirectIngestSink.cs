using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：直接寫進本機可連到的資料庫。這是原本
/// WebhookEventHandler.HandleEventAsync 落地那一半邏輯搬過來的結果，行為刻意保持一致——
/// 差別只在輸入從 WebhookEvent 換成已經解析過的 IngestEnvelope。
///
/// 刻意不注入 IContentDownloadQueue／IProfileRefreshQueue：入列是「這台主機要不要對這筆
/// 資料做後續處理」的決定，兩個呼叫端（IngestController、OutboxForwarderService）各自跑在
/// 不同主機、各自持有本機該不該做事的那份佇列（真 Channel 或 Null 實作視 Line:OutboundHere
/// 而定），由它們在拿到 IngestResult 後統一呼叫 IngestSideEffects 處理。若讓這裡也自己入列，
/// Full 模式下 OutboxForwarderService 直接呼叫這個類別時會造成同一筆內容被入列兩次
/// （一次在這裡、一次在呼叫端）——這正是 Stage 3 把 ContentId 帶出 IngestResult 之後
/// 才浮現的問題，見 docs/DEPLOYMENT-MODES.md 的相關設計決策。</summary>
public class DirectIngestSink(
    MessageDbContext dbContext,
    ILogger<DirectIngestSink> logger) : IIngestSink
{
    public async Task<IngestResult> SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        // 防重送的真正保證是 GroupMessages.WebhookEventId 的唯一索引（見 MessageDbContext）
        // 加下面的 DbUpdateException 攔截；這句預查只是省一次白跑的 INSERT，不是正確性來源——
        // outbox 重試、或 Line 模式重送同一個 WebhookEventId 時，沒有這句預查也不會產生重複資料。
        // 順便投影出既有那筆的 Content.Id：重複也要把 ContentId 帶回去（見 IIngestSink 說明），
        // 不然 outbox 重試（代表前一次的回應可能遺失了）會讓拆機模式的那筆媒體卡到下次重啟才補回。
        // 投影成含 m.Id 的物件而不是直接投影 ContentId：「查無此列」（整個投影結果是 null）跟
        // 「該列存在但沒有媒體內容」（ContentId 欄位是 null）必須分得清楚，只投影 ContentId
        // 兩種情況都會是 null，無從分辨要不要繼續往下插入新列
        var existing = await dbContext.GroupMessages
            .Where(m => m.WebhookEventId == envelope.WebhookEventId)
            .Select(m => new { m.Id, ContentId = (long?)m.Content!.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Skipping duplicate webhook event {WebhookEventId}", envelope.WebhookEventId);
            return new IngestResult(existing.ContentId);
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
                .Where(m => m.WebhookEventId == envelope.WebhookEventId)
                .Select(m => new { m.Id, ContentId = (long?)m.Content!.Id })
                .FirstOrDefaultAsync(cancellationToken);
            if (saved is not null)
            {
                // 資料在（撞鍵、或前一次嘗試其實已 commit 才斷線）→ 當重複處理，
                // 回傳既有那筆的 ContentId（理由同上面的預查）。
                // 後者若有 Pending 內容，這次不會補入列，由下次服務重啟的啟動接續撈回
                logger.LogInformation(ex, "Webhook event {WebhookEventId} already persisted, treating as duplicate", envelope.WebhookEventId);
                return new IngestResult(saved.ContentId);
            }

            logger.LogWarning(ex, "Transient failure saving webhook event {WebhookEventId}, leaving in outbox for retry", envelope.WebhookEventId);
            throw;
        }

        // 這裡不再說「content download queued」——入列與否是呼叫端（IngestController／
        // OutboxForwarderService）拿到下面這個 ContentId 之後才決定的事，這個方法本身
        // 只負責寫資料庫，見類別註解
        logger.LogInformation("Saved {MessageType} message {LineMessageId} from group {GroupId}{HasContent}",
            envelope.MessageType, groupMessage.LineMessageId, groupMessage.GroupId,
            groupMessage.Content is null ? "" : " (has pending content)");

        await TrackGroupLastMessageAsync(groupMessage, cancellationToken);

        return new IngestResult(groupMessage.Content?.Id);
    }

    /// <summary>側欄反正規化（見 GroupLastMessageTracker）：獨立於訊息本身的 SaveChanges，
    /// 失敗不該讓整個 ingest 請求失敗——訊息已經確實存進去了，側欄的 LastMessageId 這次
    /// 沒更新到，下一則訊息進來時會自然追上。</summary>
    private async Task TrackGroupLastMessageAsync(GroupMessage groupMessage, CancellationToken cancellationToken)
    {
        try
        {
            await GroupLastMessageTracker.TrackAsync(
                dbContext, groupMessage.GroupId, groupMessage.Id, groupMessage.EventTimestamp, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Groups 主鍵撞鍵：跟 DbProfileStore 的頭貼快取寫入路徑（ProfileRefreshService／
            // IngestController 的 profile upsert）併發時，兩邊都判定「這個群組還沒有列」同時
            // 插入——對方贏了，這裡改成 UPDATE 補上 LastMessageId/At
            dbContext.ChangeTracker.Clear();
            try
            {
                await GroupLastMessageTracker.TrackAsync(
                    dbContext, groupMessage.GroupId, groupMessage.Id, groupMessage.EventTimestamp, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException retryEx)
            {
                // 理論上不會再撞——重試前已經確認過對方贏了才會走到這裡。真的又失敗只記警告，
                // 不讓側欄統計的次要問題擋住整個 ingest 請求成功
                logger.LogWarning(retryEx,
                    "Failed to track last message for group {GroupId} after retry (original: {OriginalError})",
                    groupMessage.GroupId, ex.Message);
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
