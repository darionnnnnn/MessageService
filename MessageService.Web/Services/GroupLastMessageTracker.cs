using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>維護 Groups.LastMessageId／LastMessageAt——側欄反正規化的寫入端，讓
/// GroupsController 不用再對 GroupMessages 全表做 GroupBy+Max。DirectIngestSink 每筆訊息
/// 落地後即時呼叫；測試 seeding（WebAppFactoryFixture）批次事後呼叫同一份邏輯，兩邊共用
/// 同一個實作，不留兩份會漂移的複本。</summary>
public static class GroupLastMessageTracker
{
    /// <summary>只在新訊息 Id 大於目前記錄值時才更新，讓這個方法無論用什麼順序呼叫都安全
    /// （即時逐筆、或測試批次事後補算皆可）。GroupName／PictureUrl 完全不碰，那是頭貼快取
    /// （DbProfileStore）的職責，這裡沒有資料就留 null，之後頭貼快取自然補上；stub 列的
    /// UpdatedAt 給 DateTimeOffset 最小值，讓頭貼快取的過期判斷立刻視為需要刷新。
    ///
    /// 呼叫端自行負責 SaveChangesAsync／DbUpdateException 重試（見 DirectIngestSink）——這裡
    /// 只改 change tracker 上的狀態，不落地，才能跟訊息本身的寫入共用交易邊界判斷。</summary>
    public static async Task TrackAsync(
        MessageDbContext dbContext, string groupId, long messageId, DateTimeOffset eventTimestamp, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups.FindAsync([groupId], cancellationToken);
        if (group is null)
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = groupId,
                UpdatedAt = DateTimeOffset.MinValue,
                LastMessageId = messageId,
                LastMessageAt = eventTimestamp,
            });
            return;
        }

        if (group.LastMessageId is null || messageId > group.LastMessageId)
        {
            group.LastMessageId = messageId;
            group.LastMessageAt = eventTimestamp;
        }
    }
}
