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
                g.PictureUrl,
                g.PictureFetchedUrl,
                HasPicture = g.Picture != null
            })
            .FirstOrDefaultAsync(cancellationToken);

        var groupStale = group is null
            || IsStale(group.UpdatedAt, group.PictureUrl, group.PictureFetchedUrl, cutoff);
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
                m.PictureUrl,
                m.PictureFetchedUrl,
                HasPicture = m.Picture != null
            })
            .FirstOrDefaultAsync(cancellationToken);

        var memberStale = member is null
            || IsStale(member.UpdatedAt, member.PictureUrl, member.PictureFetchedUrl, cutoff);
        return new ProfileStaleness(groupStale, memberStale, groupFetchedUrl, member?.PictureFetchedUrl, hasGroupPicture, member?.HasPicture ?? false);
    }

    /// <summary>除了 TTL，只要 LINE 回傳的 PictureUrl 與我們成功抓取或記錄過的 PictureFetchedUrl 不同，
    /// 就視為過期需要重新抓取。
    ///
    /// 為什麼不檢查「是否有圖」：成員換頭貼後 PictureUrl 為新網址，但若下載暫時失敗，
    /// 名稱仍會寫入且 UpdatedAt 會被推到現在；此時舊的 blob 仍在，若檢查 hasPicture 會讓缺圖子句
    /// 被短路而判定「不過期」，導致畫面上要等滿 RefreshAfter（預設 7 天）才會換成新頭貼。
    /// 因此改為只要 PictureUrl 與 PictureFetchedUrl 不同即算過期。
    /// 永久失敗的情況（例如檔案超過上限或 404）會將 PictureFetchedUrl 標記為該網址，
    /// 仍由 PictureFetchedUrl == PictureUrl 擋住，不會變成無限重抓。</summary>
    private static bool IsStale(
        DateTimeOffset updatedAt, string? pictureUrl, string? pictureFetchedUrl, DateTimeOffset cutoff) =>
        updatedAt < cutoff
        || (!string.IsNullOrWhiteSpace(pictureUrl)
            && !string.Equals(pictureUrl, pictureFetchedUrl, StringComparison.Ordinal));

    public async Task<IReadOnlyList<ProfileRefreshTask>> GetStaleProfilesAsync(
        int max, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (max <= 0)
        {
            return [];
        }

        var candidateGroups = await dbContext.Groups
            .Where(g => g.UpdatedAt < cutoff
                || (!string.IsNullOrWhiteSpace(g.PictureUrl)
                    && g.Picture == null
                    && (g.PictureFetchedUrl == null || g.PictureFetchedUrl != g.PictureUrl)))
            .OrderBy(g => g.UpdatedAt)
            .Take(max)
            .Select(g => g.GroupId)
            .ToListAsync(cancellationToken);

        var remaining = max - candidateGroups.Count;
        List<(string GroupId, string UserId)> candidateMembers = [];
        if (remaining > 0)
        {
            candidateMembers = await dbContext.GroupMembers
                .Where(m => m.UpdatedAt < cutoff
                    || (!string.IsNullOrWhiteSpace(m.PictureUrl)
                        && m.Picture == null
                        && (m.PictureFetchedUrl == null || m.PictureFetchedUrl != m.PictureUrl)))
                .OrderBy(m => m.UpdatedAt)
                .Take(remaining)
                .Select(m => new ValueTuple<string, string>(m.GroupId, m.UserId))
                .ToListAsync(cancellationToken);
        }

        var results = new List<ProfileRefreshTask>(candidateGroups.Count + candidateMembers.Count);
        foreach (var groupId in candidateGroups)
        {
            results.Add(new ProfileRefreshTask(groupId, null));
        }
        foreach (var (groupId, userId) in candidateMembers)
        {
            results.Add(new ProfileRefreshTask(groupId, userId));
        }

        return results;
    }

    public async Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        // Groups 主鍵撞鍵：跟 DirectIngestSink 的 GroupLastMessageTracker（訊息落地時建
        // stub 列）併發或多主機同時寫入時，兩邊都判定「這個群組還沒有列」同時插入——對方贏了，這裡改成
        // UPDATE。不像 GroupLastMessageTracker 那樣把失敗吞掉：頭貼資料是使用者直接看得到
        // 的內容，重試失敗要讓呼叫端知道（IngestController／ProfileRefreshService 本來就有
        // 各自的重試與錯誤處理）。
        await ExecuteWithRetryOnDbUpdateExceptionAsync(
            () => ApplyGroupUpsertAsync(groupId, summary, cancellationToken),
            ex => logger.LogInformation(ex, "Group {GroupId} row created concurrently, retrying as update", groupId));
    }

    private async Task ApplyGroupUpsertAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Groups.FindAsync([groupId], cancellationToken);
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
            else if (summary.PicturePermanentlyUnavailable)
            {
                entity.PictureFetchedUrl = summary.PictureUrl;
            }
            dbContext.Groups.Add(entity);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(summary.GroupName))
            {
                existing.GroupName = summary.GroupName;
            }
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
            else if (summary.PicturePermanentlyUnavailable)
            {
                existing.PictureFetchedUrl = summary.PictureUrl;
            }
            else if (string.IsNullOrWhiteSpace(summary.PictureUrl))
            {
                await DeleteGroupPictureRowAsync(groupId, cancellationToken);
                ClearPictureMetadata(existing);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        // GroupMembers 複合主鍵 (GroupId, UserId) 撞鍵：多主機環境下 Core 與 Edge 都可能是 OutboundHere，
        // 兩台會同時為同一使用者 upsert 頭貼與名稱資料。兩端同時判定實體不存在並插入時，較慢的一方會撞上主鍵衝突。
        // 透過共用重試機制，在撞鍵時清空 ChangeTracker 並重新查詢以改走 UPDATE 重試一次；第二次若仍失敗則向外拋出。
        await ExecuteWithRetryOnDbUpdateExceptionAsync(
            () => ApplyMemberUpsertAsync(groupId, userId, profile, cancellationToken),
            ex => logger.LogInformation(ex, "Member {GroupId}/{UserId} row created concurrently, retrying as update", groupId, userId));
    }

    private async Task ApplyMemberUpsertAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        var existing = await dbContext.GroupMembers.FindAsync([groupId, userId], cancellationToken);
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
            else if (profile.PicturePermanentlyUnavailable)
            {
                entity.PictureFetchedUrl = profile.PictureUrl;
            }
            dbContext.GroupMembers.Add(entity);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                existing.DisplayName = profile.DisplayName;
            }
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
            else if (profile.PicturePermanentlyUnavailable)
            {
                existing.PictureFetchedUrl = profile.PictureUrl;
            }
            else if (string.IsNullOrWhiteSpace(profile.PictureUrl))
            {
                await DeleteMemberPictureRowAsync(groupId, userId, cancellationToken);
                ClearPictureMetadata(existing);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 當多主機或多行程同時 upsert 同一筆資料（例如群組或成員）時，兩方可能同時判定列不存在而嘗試 INSERT，
    /// 導致較慢的一方在 SaveChangesAsync 時拋出 DbUpdateException 主鍵衝突。
    /// 此輔助方法在捕捉到 DbUpdateException 時先清除 ChangeTracker 追蹤狀態，記錄日誌後重新執行一次（改為 UPDATE）；
    /// 若重試第二次仍然失敗則向外拋出例外，交由呼叫端錯誤處理機制。其他非 DbUpdateException 例外則原樣往外拋。
    /// </summary>
    private async Task ExecuteWithRetryOnDbUpdateExceptionAsync(Func<Task> action, Action<DbUpdateException> onRetry)
    {
        try
        {
            await action();
        }
        catch (DbUpdateException ex)
        {
            dbContext.ChangeTracker.Clear();
            onRetry(ex);
            await action();
        }
    }

    /// <summary>頭貼子列的 upsert。刻意不用 Include 把既有子列連同 blob 撈回來判斷存不存在
    /// （那是這批拆表要根治的問題）——存在性由呼叫端用 AnyAsync 查，已存在就 Attach 一顆只帶
    /// 主鍵的空殼、只標 Content 為已修改，EF 產生的 UPDATE 只會寫那一欄。</summary>
    private void UpsertPictureRow<T>(T picture, bool alreadyExists) where T : class
    {
        if (alreadyExists)
        {
            dbContext.Attach(picture);
            dbContext.Entry(picture).Property(nameof(GroupPicture.Content)).IsModified = true;
        }
        else
        {
            dbContext.Add(picture);
        }
    }

    private async Task DeleteGroupPictureRowAsync(string groupId, CancellationToken cancellationToken)
    {
        var local = dbContext.GroupPictures.Local.FirstOrDefault(p => p.GroupId == groupId);
        if (local is not null)
        {
            dbContext.GroupPictures.Remove(local);
        }
        else if (await dbContext.GroupPictures.AnyAsync(p => p.GroupId == groupId, cancellationToken))
        {
            dbContext.GroupPictures.Remove(new GroupPicture { GroupId = groupId, Content = [] });
        }
    }

    private async Task DeleteMemberPictureRowAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        var local = dbContext.GroupMemberPictures.Local.FirstOrDefault(p => p.GroupId == groupId && p.UserId == userId);
        if (local is not null)
        {
            dbContext.GroupMemberPictures.Remove(local);
        }
        else if (await dbContext.GroupMemberPictures.AnyAsync(p => p.GroupId == groupId && p.UserId == userId, cancellationToken))
        {
            dbContext.GroupMemberPictures.Remove(new GroupMemberPicture { GroupId = groupId, UserId = userId, Content = [] });
        }
    }

    private static void ClearPictureMetadata(Group group)
    {
        group.PictureContentType = null;
        group.PictureFetchedUrl = null;
        group.PictureUpdatedAt = null;
    }

    private static void ClearPictureMetadata(GroupMember member)
    {
        member.PictureContentType = null;
        member.PictureFetchedUrl = null;
        member.PictureUpdatedAt = null;
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
