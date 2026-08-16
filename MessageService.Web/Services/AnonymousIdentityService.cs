using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class AnonymousIdentityService(MessageDbContext dbContext) : IAnonymousIdentityService
{
    private const int MaxCollisionRetries = 50;

    public async Task<IReadOnlyDictionary<string, AnonymousIdentityInfo>> GetOrAssignAsync(
        string groupId, IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, AnonymousIdentityInfo>();
        if (userIds.Count == 0)
        {
            return result;
        }

        var existing = await dbContext.AnonymousIdentities
            .Where(a => a.GroupId == groupId && userIds.Contains(a.UserId))
            .ToListAsync(cancellationToken);
        foreach (var identity in existing)
        {
            result[identity.UserId] = new AnonymousIdentityInfo(identity.IconKey, identity.Label);
        }

        var missing = userIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return result;
        }

        // 序號基準：同群組每個 IconKey 已經指派過幾次，新成員接續編號（小熊 → 小熊 2）
        var iconUsageCounts = await dbContext.AnonymousIdentities
            .Where(a => a.GroupId == groupId)
            .GroupBy(a => a.IconKey)
            .Select(g => new { IconKey = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.IconKey, g => g.Count, cancellationToken);

        // 一個一個 insert 而不是整批 SaveChanges，這樣併發衝突只影響撞名的那一筆，
        // 不會讓同批裡其他原本不衝突的新指派也被整批 rollback 掉
        foreach (var userId in missing)
        {
            var icon = AvatarIconCatalog.ForHash($"{groupId}:{userId}");
            var usedCount = iconUsageCounts.GetValueOrDefault(icon.IconKey, 0);
            var retries = 0;
            AnonymousIdentityInfo? assignedInfo = null;

            while (assignedInfo == null)
            {
                var label = usedCount == 0 ? icon.Label : $"{icon.Label} {usedCount + 1}";

                var identity = new AnonymousIdentity
                {
                    GroupId = groupId,
                    UserId = userId,
                    IconKey = icon.IconKey,
                    Label = label,
                    AssignedAt = DateTimeOffset.UtcNow
                };
                dbContext.AnonymousIdentities.Add(identity);

                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    // 存檔成功，更新計數以供同批後續成員使用
                    usedCount++;
                    iconUsageCounts[icon.IconKey] = usedCount;
                    assignedInfo = new AnonymousIdentityInfo(identity.IconKey, identity.Label);
                }
                catch (DbUpdateException)
                {
                    // 存檔失敗時先將實體從追蹤狀態中分離，避免殘留在 Added 狀態影響後續操作
                    dbContext.Entry(identity).State = EntityState.Detached;

                    // 存檔遇到 DbUpdateException 時進行三路判斷：
                    // 1. 同一個 (GroupId, UserId) 已存在（別的併發請求搶先指派了這個人）：
                    //    直接採用資料庫裡既有的那一筆（IconKey 與 Label），不再重試。
                    var existingUserIdentity = await dbContext.AnonymousIdentities
                        .AsNoTracking()
                        .FirstOrDefaultAsync(a => a.GroupId == groupId && a.UserId == userId, cancellationToken);

                    if (existingUserIdentity != null)
                    {
                        usedCount++;
                        iconUsageCounts[icon.IconKey] = Math.Max(iconUsageCounts.GetValueOrDefault(icon.IconKey, 0), usedCount);
                        assignedInfo = new AnonymousIdentityInfo(existingUserIdentity.IconKey, existingUserIdentity.Label);
                        break;
                    }

                    // 2. 代號撞名（同群組已有其他成員佔用此 Label，觸發 (GroupId, Label) 唯一索引衝突）：
                    //    後綴遞增後重試存檔，重試上限 50 次；超過上限拋出帶清楚說明的 InvalidOperationException。
                    var isLabelCollided = await dbContext.AnonymousIdentities
                        .AnyAsync(a => a.GroupId == groupId && a.Label == label, cancellationToken);

                    if (isLabelCollided)
                    {
                        retries++;
                        if (retries > MaxCollisionRetries)
                        {
                            throw new InvalidOperationException(
                                $"群組 '{groupId}' 為使用者 '{userId}' 指派匿名代號時發生撞名衝突，已嘗試 {MaxCollisionRetries} 次仍未成功。");
                        }

                        usedCount++;
                        iconUsageCounts[icon.IconKey] = usedCount;
                        continue;
                    }

                    // 3. 以上皆非（與衝突無關的暫時性故障，例如連線中斷、逾時、資料庫不可用等）：
                    //    必須把原本的 DbUpdateException 往外拋，絕不能吞掉或誤當作撞名／查無資料，
                    //    避免將底層連線問題偽裝成奇怪的業務邏輯錯誤。
                    throw;
                }
            }

            result[userId] = assignedInfo;
        }

        return result;
    }
}
