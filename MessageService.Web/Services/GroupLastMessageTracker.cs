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
    /// 只改 change tracker 上的狀態，不落地，才能跟訊息本身的寫入共用交易邊界判斷。
    ///
    /// 更新既有群組時刻意不載入整個 Group（收訊息的必經路徑，每則訊息都會走一次），改成
    /// Attach 一顆只帶主鍵的空殼、只把那兩個欄位標成已修改，EF 產生的 UPDATE 只含那兩欄。
    /// 代價是這顆空殼會留在 change tracker 裡，其餘欄位是 null／預設值——**同一個 DbContext
    /// 在這之後不可以直接讀 Group 的其他欄位**（EF 的 identity map 不會用查詢結果覆寫已追蹤
    /// 實體的屬性，會讀到假的 null）。目前的呼叫端都是「呼叫完就 SaveChanges 收工」或撞鍵時
    /// ChangeTracker.Clear() 重來，符合這個前提；日後要在同一個 scope 裡加讀取，先看這裡。</summary>
    public static async Task TrackAsync(
        MessageDbContext dbContext, string groupId, long messageId, DateTimeOffset eventTimestamp, CancellationToken cancellationToken)
    {
        var trackedEntry = dbContext.ChangeTracker.Entries<Group>().FirstOrDefault(e => e.Entity.GroupId == groupId);
        if (trackedEntry is not null)
        {
            if (trackedEntry.Entity.LastMessageId is null || messageId > trackedEntry.Entity.LastMessageId)
            {
                trackedEntry.Entity.LastMessageId = messageId;
                trackedEntry.Entity.LastMessageAt = eventTimestamp;
                if (trackedEntry.State != EntityState.Added)
                {
                    trackedEntry.Property(g => g.LastMessageId).IsModified = true;
                    trackedEntry.Property(g => g.LastMessageAt).IsModified = true;
                }
            }
            return;
        }

        var groupInfo = await dbContext.Groups
            .Where(g => g.GroupId == groupId)
            .Select(g => new { g.LastMessageId })
            .FirstOrDefaultAsync(cancellationToken);

        if (groupInfo is null)
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

        if (groupInfo.LastMessageId is null || messageId > groupInfo.LastMessageId)
        {
            var stub = new Group { GroupId = groupId };
            var entry = dbContext.Groups.Attach(stub);
            stub.LastMessageId = messageId;
            stub.LastMessageAt = eventTimestamp;
            entry.Property(g => g.LastMessageId).IsModified = true;
            entry.Property(g => g.LastMessageAt).IsModified = true;
        }
    }
}
