using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class AnonymousIdentityService(MessageDbContext dbContext) : IAnonymousIdentityService
{
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
            }
            catch (DbUpdateException)
            {
                // 另一個併發請求搶先指派過這個人了：丟掉這筆本地嘗試，改讀對方寫入的那筆
                dbContext.Entry(identity).State = EntityState.Detached;
                identity = await dbContext.AnonymousIdentities
                    .FirstAsync(a => a.GroupId == groupId && a.UserId == userId, cancellationToken);
            }

            iconUsageCounts[icon.IconKey] = usedCount + 1;
            result[userId] = new AnonymousIdentityInfo(identity.IconKey, identity.Label);
        }

        return result;
    }
}
