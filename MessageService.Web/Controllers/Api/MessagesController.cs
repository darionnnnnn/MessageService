using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

[ApiController]
public class MessagesController(
    MessageDbContext dbContext,
    ContentStreamService contentStreamService,
    IMaskingService maskingService,
    IAnonymousIdentityService anonymousIdentityService) : ControllerBase
{
    public const int MaxDays = 3650;

    [HttpGet("api/groups/{groupId}/messages")]
    public async Task<ActionResult<MessagesPageDto>> GetMessages(
        string groupId,
        [FromQuery] int days = 3,
        [FromQuery] long? beforeId = null,
        [FromQuery] long? afterId = null,
        CancellationToken cancellationToken = default)
    {
        // 上限刻意遠高於收錄端的保留年限（預設 3 年），這樣前端在沒有游標可用時
        // 靠放大天數視窗也一定能觸及所有仍保留的訊息
        days = Math.Clamp(days, 1, MaxDays);

        IQueryable<GroupMessage> query = dbContext.GroupMessages.Where(m => m.GroupId == groupId);

        if (afterId is { } after)
        {
            query = query.Where(m => m.Id > after);
        }
        else if (beforeId is { } before)
        {
            var cursor = await dbContext.GroupMessages
                .Where(m => m.Id == before)
                .Select(m => new { m.EventTimestamp })
                .FirstOrDefaultAsync(cancellationToken);

            if (cursor is null)
            {
                return NotFound();
            }

            // 下一則更早訊息（依 Id，即實際到達順序）。若它跟游標之間的空窗比 days 還長，
            // 就改以它為基準開窗；否則群組沉寂超過一個視窗時，查詢永遠回空、游標不會前進，
            // 使用者會一直按「載入更早」卻什麼都不會出現
            var nextOlder = await dbContext.GroupMessages
                .Where(m => m.GroupId == groupId && m.Id < before)
                .OrderByDescending(m => m.Id)
                .Select(m => new { m.EventTimestamp })
                .FirstOrDefaultAsync(cancellationToken);

            var anchor = cursor.EventTimestamp;
            var plainCutoff = anchor.AddDays(-days);
            if (nextOlder is not null && nextOlder.EventTimestamp < plainCutoff)
            {
                anchor = nextOlder.EventTimestamp;
            }

            var cutoff = anchor.AddDays(-days);
            query = query.Where(m => m.Id < before && m.EventTimestamp >= cutoff);
        }
        else
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            query = query.Where(m => m.EventTimestamp >= cutoff);
        }

        var rows = await query
            .OrderBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.MessageType,
                m.Text,
                m.UserId,
                m.EventTimestamp,
                Content = m.Content == null
                    ? null
                    : new { m.Content.Id, m.Content.FileName, m.Content.ContentType, m.Content.DownloadStatus }
            })
            .ToListAsync(cancellationToken);

        var userIds = rows.Select(r => r.UserId).Where(id => id is not null).Cast<string>().Distinct().ToList();
        var members = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId && userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, cancellationToken);

        // 一個請求只載入一次遮蔽規則，套用到每則訊息時全是同步運算，不會每則訊息各打一次 DB
        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        // 只有 Anonymous 模式才需要查/指派永久代號；其他模式完全不打這張表
        IReadOnlyDictionary<string, AnonymousIdentityInfo> anonymousIdentities =
            new Dictionary<string, AnonymousIdentityInfo>();
        if (maskingRules.RequiresAnonymousIdentity)
        {
            anonymousIdentities = await anonymousIdentityService.GetOrAssignAsync(groupId, userIds, cancellationToken);
        }

        var messages = rows.Select(r =>
        {
            var text = r.Text is null ? null : maskingRules.MaskText(groupId, r.Text);
            var content = r.Content is null
                ? null
                : new MessageContentDto(r.Content.Id, r.Content.FileName, r.Content.ContentType, r.Content.DownloadStatus.ToString());

            if (r.UserId is null)
            {
                return new MessageDto(r.Id, r.MessageType, text, null, "(未知)", r.EventTimestamp, content, null, null);
            }

            members.TryGetValue(r.UserId, out var member);

            string displayName;
            string? pictureUrl;
            string avatarIcon;
            if (maskingRules.RequiresAnonymousIdentity)
            {
                var identity = anonymousIdentities[r.UserId];
                displayName = maskingRules.ResolveDisplayName(r.UserId, member?.DisplayName, identity.Label);
                pictureUrl = null;
                avatarIcon = identity.IconKey;
            }
            else
            {
                displayName = maskingRules.ResolveDisplayName(r.UserId, member?.DisplayName);
                // 非 Original 模式下真實頭貼一律不外流，即使前端不渲染，URL 本身就是身分線索
                pictureUrl = maskingRules.RevealsOriginalProfile ? member?.PictureUrl : null;
                // 一律附上決定性的 fallback 圖示 key，前端在 PictureUrl 缺失或載入失敗時可以直接換上
                avatarIcon = AvatarIconCatalog.ForHash(r.UserId).IconKey;
            }

            return new MessageDto(r.Id, r.MessageType, text, r.UserId, displayName, r.EventTimestamp, content, pictureUrl, avatarIcon);
        }).ToList();

        // hasMore：初載/往前加載都要判斷是否還有更早的訊息；輪詢（afterId）不需要，省一次查詢
        var oldestFetchedId = messages.Count > 0 ? messages[0].Id : beforeId ?? long.MaxValue;
        var hasMore = afterId is null &&
            await dbContext.GroupMessages.AnyAsync(m => m.GroupId == groupId && m.Id < oldestFetchedId, cancellationToken);

        // 初載時即使畫面上顯示的天數視窗內剛好沒有訊息，前端輪詢仍需要一個基準 id 才能偵測後續新訊息；
        // 往前加載/輪詢本身不需要，只有初載才算，省不必要的查詢
        long? latestId = null;
        if (beforeId is null && afterId is null)
        {
            latestId = await dbContext.GroupMessages
                .Where(m => m.GroupId == groupId)
                .Select(m => (long?)m.Id)
                .MaxAsync(cancellationToken);
        }

        return Ok(new MessagesPageDto(messages, hasMore, latestId));
    }

    [HttpGet("api/messages/{id:long}/content")]
    public async Task<IActionResult> GetContent(long id, CancellationToken cancellationToken)
    {
        var rangeHeader = Request.Headers.Range.ToString();
        var result = await contentStreamService.StreamAsync(
            id, string.IsNullOrEmpty(rangeHeader) ? null : rangeHeader, Response, cancellationToken);

        return result == ContentStreamResult.NotFound ? NotFound() : new EmptyResult();
    }

    [HttpGet("api/messages/statuses")]
    public async Task<ActionResult<IReadOnlyList<MessageStatusDto>>> GetStatuses(
        [FromQuery] string? ids, CancellationToken cancellationToken)
    {
        var contentIds = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        if (contentIds.Count == 0)
        {
            return Ok(Array.Empty<MessageStatusDto>());
        }

        var statuses = await dbContext.MessageContents
            .Where(c => contentIds.Contains(c.Id))
            .Select(c => new MessageStatusDto(c.Id, c.DownloadStatus.ToString(), c.ContentType))
            .ToListAsync(cancellationToken);

        return Ok(statuses);
    }
}
