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
        var existing = await dbContext.Groups.FirstOrDefaultAsync(g => g.GroupId == groupId, cancellationToken);
        if (existing is null)
        {
            var entity = new Group
            {
                GroupId = groupId,
                GroupName = summary.GroupName,
                PictureUrl = summary.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            if (summary.PictureBytes != null)
            {
                entity.Picture = new GroupPicture { GroupId = groupId, Content = EncryptPictureContent(summary.PictureBytes) };
                ApplyPictureMetadata(entity, summary.PictureUrl, summary.PictureContentType);
            }
            dbContext.Groups.Add(entity);
        }
        else
        {
            existing.GroupName = summary.GroupName;
            existing.PictureUrl = summary.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            if (summary.PictureBytes != null)
            {
                var picture = new GroupPicture { GroupId = groupId, Content = EncryptPictureContent(summary.PictureBytes) };
                UpsertPictureRow(
                    picture,
                    await dbContext.GroupPictures.AnyAsync(p => p.GroupId == groupId, cancellationToken));
                ApplyPictureMetadata(existing, summary.PictureUrl, summary.PictureContentType);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        var existing = await dbContext.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);
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
            if (profile.PictureBytes != null)
            {
                entity.Picture = new GroupMemberPicture { GroupId = groupId, UserId = userId, Content = EncryptPictureContent(profile.PictureBytes) };
                ApplyPictureMetadata(entity, profile.PictureUrl, profile.PictureContentType);
            }
            dbContext.GroupMembers.Add(entity);
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.PictureUrl = profile.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            if (profile.PictureBytes != null)
            {
                var picture = new GroupMemberPicture { GroupId = groupId, UserId = userId, Content = EncryptPictureContent(profile.PictureBytes) };
                UpsertPictureRow(
                    picture,
                    await dbContext.GroupMemberPictures.AnyAsync(p => p.GroupId == groupId && p.UserId == userId, cancellationToken));
                ApplyPictureMetadata(existing, profile.PictureUrl, profile.PictureContentType);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>頭貼子列的 upsert。刻意不用 Include 把既有子列連同 blob 撈回來判斷存不存在
    /// （那是這批拆表要根治的問題）——存在性由呼叫端用 AnyAsync 查，已存在就 Attach 一顆只帶
    /// 主鍵的空殼、只標 Content 為已修改，EF 產生的 UPDATE 只會寫那一欄。</summary>
    private void UpsertPictureRow<T>(T picture, bool alreadyExists) where T : class
    {
        if (alreadyExists)
        {
            dbContext.Attach(picture);
            dbContext.Entry(picture).Property("Content").IsModified = true;
        }
        else
        {
            dbContext.Add(picture);
        }
    }

    /// <summary>頭貼的中繼欄位一律跟著 blob 一起更新，避免只改一邊造成「有圖但中繼資料是舊的」。
    /// PictureUpdatedAt 對齊該實體這次的 UpdatedAt，兩者本來就是同一次刷新。</summary>
    private static void ApplyPictureMetadata(Group group, string? pictureUrl, string? contentType)
    {
        group.PictureContentType = contentType;
        group.PictureFetchedUrl = pictureUrl;
        group.PictureUpdatedAt = group.UpdatedAt;
    }

    private static void ApplyPictureMetadata(GroupMember member, string? pictureUrl, string? contentType)
    {
        member.PictureContentType = contentType;
        member.PictureFetchedUrl = pictureUrl;
        member.PictureUpdatedAt = member.UpdatedAt;
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
