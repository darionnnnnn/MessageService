using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ProfileRefreshService 原本直接開 scope 拿 MessageDbContext
/// 判斷 TTL、upsert 的那段邏輯搬過來，行為刻意保持一致。</summary>
public class DbProfileStore(MessageDbContext dbContext, ILogger<DbProfileStore> logger) : IProfileStore
{
    public async Task<ProfileStaleness> GetStalenessAsync(
        string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups.FindAsync([groupId], cancellationToken);
        var groupStale = group is null || group.UpdatedAt < cutoff;

        if (userId is null)
        {
            return new ProfileStaleness(groupStale, false);
        }

        var member = await dbContext.GroupMembers.FindAsync([groupId, userId], cancellationToken);
        var memberStale = member is null || member.UpdatedAt < cutoff;
        return new ProfileStaleness(groupStale, memberStale);
    }

    public async Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyGroupUpsertAsync(groupId, summary, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Groups 主鍵撞鍵：跟 DirectIngestSink 的 GroupLastMessageTracker（訊息落地時建
            // stub 列）併發時，兩邊都判定「這個群組還沒有列」同時插入——對方贏了，這裡改成
            // UPDATE。不像 GroupLastMessageTracker 那樣把失敗吞掉：頭貼資料是使用者直接看得到
            // 的內容，重試失敗要讓呼叫端知道（IngestController／ProfileRefreshService 本來就有
            // 各自的重試與錯誤處理）。
            dbContext.ChangeTracker.Clear();
            logger.LogInformation(ex, "Group {GroupId} row created concurrently, retrying as update", groupId);
            await ApplyGroupUpsertAsync(groupId, summary, cancellationToken);
        }
    }

    private async Task ApplyGroupUpsertAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Groups.FindAsync([groupId], cancellationToken);
        if (existing is null)
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = groupId,
                GroupName = summary.GroupName,
                PictureUrl = summary.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.GroupName = summary.GroupName;
            existing.PictureUrl = summary.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        var existing = await dbContext.GroupMembers.FindAsync([groupId, userId], cancellationToken);
        if (existing is null)
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = profile.DisplayName,
                PictureUrl = profile.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.PictureUrl = profile.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
