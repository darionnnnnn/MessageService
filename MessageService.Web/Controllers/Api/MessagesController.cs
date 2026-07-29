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
    IMaskingService maskingService) : ControllerBase
{
    [HttpGet("api/groups/{groupId}/messages")]
    public async Task<ActionResult<MessagesPageDto>> GetMessages(
        string groupId,
        [FromQuery] int days = 3,
        [FromQuery] long? beforeId = null,
        [FromQuery] long? afterId = null,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 365);

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

            var cutoff = cursor.EventTimestamp.AddDays(-days);
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
            .Where(m => m.GroupId == groupId && userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, cancellationToken);

        var messages = rows.Select(r =>
        {
            members.TryGetValue(r.UserId ?? "", out var member);
            var displayName = r.UserId is null ? "(未知)" : maskingService.ResolveDisplayName(r.UserId, member?.DisplayName);
            var text = r.Text is null ? null : maskingService.MaskText(groupId, r.Text);
            var content = r.Content is null
                ? null
                : new MessageContentDto(r.Content.Id, r.Content.FileName, r.Content.ContentType, r.Content.DownloadStatus.ToString());

            return new MessageDto(r.Id, r.MessageType, text, r.UserId, displayName, r.EventTimestamp, content);
        }).ToList();

        // hasMore：初載/往前加載都要判斷是否還有更早的訊息；輪詢（afterId）不需要，省一次查詢
        var oldestFetchedId = messages.Count > 0 ? messages[0].Id : beforeId ?? long.MaxValue;
        var hasMore = afterId is null &&
            await dbContext.GroupMessages.AnyAsync(m => m.GroupId == groupId && m.Id < oldestFetchedId, cancellationToken);

        return Ok(new MessagesPageDto(messages, hasMore));
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
