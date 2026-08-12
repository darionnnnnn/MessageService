using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ProfileRefreshService 原本直接開 scope 拿 MessageDbContext
/// 判斷 TTL、upsert 的那段邏輯搬過來，行為刻意保持一致。</summary>
public class DbProfileStore(MessageDbContext dbContext) : IProfileStore
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
