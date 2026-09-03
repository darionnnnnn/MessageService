using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Services;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Controllers.Api;

[ApiController]
[RequiresCapability(Capability.Viewer)]
public class AvatarsController(
    MessageDbContext dbContext,
    FieldCipher cipher,
    IMaskingService maskingService,
    ILogger<AvatarsController> logger) : ControllerBase
{
    [HttpGet("api/groups/{groupId}/avatar")]
    public async Task<IActionResult> GetGroupAvatar(string groupId, CancellationToken cancellationToken)
    {
        // 群組照片是「群組」的識別，不是個人資料；匿名／別名模式保護的是「這則訊息是誰說的」，
        // 把群組本身的照片也擋掉並沒有保護到任何人，反而讓側欄給了網址卻必定 404。成員頭貼是個人資料，維持受限。

        // 採取兩段查詢：先投影出輕量中繼資料比對 ETag，若命中 304 即可避免撈取 blob 本體；
        // 雖然 200 路徑會多一次輕量查詢，但因 no-cache 快取策略使 304 成為常態，這筆交易是划算的。
        var meta = await dbContext.Groups
            .AsNoTracking()
            .Where(g => g.GroupId == groupId)
            .Select(g => new { HasPicture = g.Picture != null, g.PictureContentType, g.PictureUpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (meta is null || !meta.HasPicture)
        {
            return NotFound();
        }

        var etag = FormatETag(groupId, meta.PictureUpdatedAt);
        if (IsIfNoneMatchHit(etag))
        {
            return NotModified(etag);
        }

        var content = await dbContext.Groups
            .AsNoTracking()
            .Where(g => g.GroupId == groupId)
            .Select(g => g.Picture == null ? null : g.Picture.Content)
            .FirstOrDefaultAsync(cancellationToken);

        if (content is null or { Length: 0 })
        {
            return NotFound();
        }

        return ProcessAvatarContent(groupId, content, meta.PictureContentType, etag);
    }

    [HttpGet("api/groups/{groupId}/members/{userId}/avatar")]
    public async Task<IActionResult> GetMemberAvatar(string groupId, string userId, CancellationToken cancellationToken)
    {
        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);
        if (!maskingRules.RevealsOriginalProfile)
        {
            return NotFound();
        }

        // 採取兩段查詢：先投影出輕量中繼資料比對 ETag，若命中 304 即可避免撈取 blob 本體；
        // 雖然 200 路徑會多一次輕量查詢，但因 no-cache 快取策略使 304 成為常態，這筆交易是划算的。
        var meta = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .Select(m => new { HasPicture = m.Picture != null, m.PictureContentType, m.PictureUpdatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (meta is null || !meta.HasPicture)
        {
            return NotFound();
        }

        var etag = FormatETag(userId, meta.PictureUpdatedAt);
        if (IsIfNoneMatchHit(etag))
        {
            return NotModified(etag);
        }

        var content = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .Select(m => m.Picture == null ? null : m.Picture.Content)
            .FirstOrDefaultAsync(cancellationToken);

        if (content is null or { Length: 0 })
        {
            return NotFound();
        }

        return ProcessAvatarContent(userId, content, meta.PictureContentType, etag);
    }

    private static string FormatETag(string idForLog, DateTimeOffset? updatedAt) =>
        $"\"avatar-{idForLog}-{updatedAt?.UtcTicks ?? 0:x}\"";

    private bool IsIfNoneMatchHit(string etag)
    {
        var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
        return !string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch == "*" || ifNoneMatch.Contains(etag));
    }

    private IActionResult NotModified(string etag)
    {
        // 304 路徑刻意不撈 blob，判不出這一筆實際有沒有加密，改用全域開關：開關開著就送 no-store。
        // 加密開啟前寫入的舊圖會因此比 200 路徑保守（no-store 而非 no-cache），方向是更嚴、不會外洩
        SetAvatarHeaders(etag, cipher.Enabled);
        return StatusCode(StatusCodes.Status304NotModified);
    }

    private void SetAvatarHeaders(string etag, bool isEncrypted)
    {
        // 加密內容送 no-store 是因為解密後的明文屬於受保護的個資，不該讓瀏覽器跨工作階段保留在磁碟快取上。
        // 未加密內容則送 private, no-cache，允許快取但每次載入都要用 ETag 重新驗證，既省頻寬又能即時套用去識別化遮蔽或更新頭貼。
        Response.Headers.CacheControl = isEncrypted ? "no-store" : "private, no-cache";
        Response.Headers.ETag = etag;
    }

    private IActionResult ProcessAvatarContent(string idForLog, byte[] content, string? contentType, string etag)
    {
        var isEncrypted = ChunkedBlobCipher.IsEncryptedHeader(content);
        if (isEncrypted)
        {
            if (!cipher.Enabled)
            {
                return NotFound();
            }

            var keyId = ChunkedBlobCipher.ReadKeyId(content);
            if (!cipher.MatchesKeyId(keyId))
            {
                logger.LogWarning("Avatar content {Id} was encrypted with a different key; treating as unavailable", idForLog);
                return NotFound();
            }

            int chunkSize = ChunkedBlobCipher.ReadChunkSize(content);
            if (chunkSize != ChunkedBlobCipher.ChunkSize)
            {
                logger.LogWarning("Avatar content {Id} has invalid chunk size; treating as unavailable", idForLog);
                return NotFound();
            }

            try
            {
                var ms = new MemoryStream();
                var cursor = ChunkedBlobCipher.HeaderSize;
                while (cursor < content.Length)
                {
                    var chunkLength = Math.Min(ChunkedBlobCipher.ChunkOnDiskOverhead + chunkSize, content.Length - cursor);
                    var chunkBytes = content.AsSpan(cursor, chunkLength);
                    var plain = cipher.DecryptChunk(chunkBytes);
                    ms.Write(plain);
                    cursor += chunkLength;
                }
                
                content = ms.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to decrypt avatar content {Id}", idForLog);
                return NotFound();
            }
        }

        SetAvatarHeaders(etag, isEncrypted);
        Response.Headers.XContentTypeOptions = "nosniff";

        var normalizedContentType = ContentStreamService.NormalizeContentType(contentType);
        var safe = ContentStreamService.IsSafeToInline(normalizedContentType);
        var finalContentType = safe ? normalizedContentType! : "application/octet-stream";

        return File(content, finalContentType);
    }
}
