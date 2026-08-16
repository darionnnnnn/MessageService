using System.IO;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ProfileRefreshService 原本直接開 scope 拿 MessageDbContext
/// 判斷 TTL、upsert 的那段邏輯搬過來，行為刻意保持一致。</summary>
public class DbProfileStore(MessageDbContext dbContext, FieldCipher cipher, ILogger<DbProfileStore> logger) : IProfileStore
{
    public async Task<ProfileStaleness> GetStalenessAsync(
        string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups
            .Where(g => g.GroupId == groupId)
            .Select(g => new
            {
                g.UpdatedAt,
                g.PictureFetchedUrl,
                HasPicture = g.Picture != null
            })
            .FirstOrDefaultAsync(cancellationToken);

        var groupStale = group is null || group.UpdatedAt < cutoff;
        var groupFetchedUrl = group?.PictureFetchedUrl;
        var hasGroupPicture = group?.HasPicture ?? false;

        if (userId is null)
        {
            return new ProfileStaleness(groupStale, false, groupFetchedUrl, null, hasGroupPicture, false);
        }

        var member = await dbContext.GroupMembers
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .Select(m => new
            {
                m.UpdatedAt,
                m.PictureFetchedUrl,
                HasPicture = m.Picture != null
            })
            .FirstOrDefaultAsync(cancellationToken);

        var memberStale = member is null || member.UpdatedAt < cutoff;
        return new ProfileStaleness(groupStale, memberStale, groupFetchedUrl, member?.PictureFetchedUrl, hasGroupPicture, member?.HasPicture ?? false);
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
        var existing = await dbContext.Groups.Include(g => g.Picture).FirstOrDefaultAsync(g => g.GroupId == groupId, cancellationToken);
        if (existing is null)
        {
            var entity = new Group
            {
                GroupId = groupId,
                GroupName = summary.GroupName,
                PictureUrl = summary.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            ApplyPicture(entity, summary.PictureBytes, summary.PictureUrl, summary.PictureContentType);
            dbContext.Groups.Add(entity);
        }
        else
        {
            existing.GroupName = summary.GroupName;
            existing.PictureUrl = summary.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            ApplyPicture(existing, summary.PictureBytes, summary.PictureUrl, summary.PictureContentType);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        var existing = await dbContext.GroupMembers.Include(m => m.Picture).FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);
        if (existing is null)
        {
            var entity = new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = profile.DisplayName,
                PictureUrl = profile.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            ApplyPicture(entity, profile.PictureBytes, profile.PictureUrl, profile.PictureContentType);
            dbContext.GroupMembers.Add(entity);
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.PictureUrl = profile.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            ApplyPicture(existing, profile.PictureBytes, profile.PictureUrl, profile.PictureContentType);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyPicture(Group entity, byte[]? bytes, string? url, string? contentType)
    {
        if (bytes != null)
        {
            var encrypted = EncryptPictureContent(bytes);
            if (entity.Picture is not null)
            {
                entity.Picture.Content = encrypted;
            }
            else
            {
                entity.Picture = new GroupPicture { GroupId = entity.GroupId, Content = encrypted };
            }
            entity.PictureContentType = contentType;
            entity.PictureFetchedUrl = url;
            entity.PictureUpdatedAt = entity.UpdatedAt;
        }
    }

    private void ApplyPicture(GroupMember entity, byte[]? bytes, string? url, string? contentType)
    {
        if (bytes != null)
        {
            var encrypted = EncryptPictureContent(bytes);
            if (entity.Picture is not null)
            {
                entity.Picture.Content = encrypted;
            }
            else
            {
                entity.Picture = new GroupMemberPicture { GroupId = entity.GroupId, UserId = entity.UserId, Content = encrypted };
            }
            entity.PictureContentType = contentType;
            entity.PictureFetchedUrl = url;
            entity.PictureUpdatedAt = entity.UpdatedAt;
        }
    }

    private byte[] EncryptPictureContent(byte[] plaintextBytes)
    {
        if (!cipher.Enabled)
        {
            return plaintextBytes;
        }
        using var source = new MemoryStream(plaintextBytes);
        using var encryptingStream = cipher.CreateEncryptingStream(source, plaintextBytes.Length);
        using var ms = new MemoryStream();
        encryptingStream.CopyTo(ms);
        return ms.ToArray();
    }
}
