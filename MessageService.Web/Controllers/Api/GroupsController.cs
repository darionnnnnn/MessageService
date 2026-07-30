using MessageService.Data;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

[ApiController]
[Route("api/groups")]
public class GroupsController(MessageDbContext dbContext, IMaskingService maskingService) : ControllerBase
{
    private const int PreviewMaxLength = 30;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> GetGroups(CancellationToken cancellationToken)
    {
        // GroupBy+Max 是 EF Core 能穩定轉譯成單一 SQL 的聚合寫法；GroupBy+OrderBy().First() 在部分
        // provider 上會退回 client evaluation，改用「先找每群組最後一則的 Id，再用 Id 撈整列」兩段式查詢
        var latestIdsByGroup = await dbContext.GroupMessages
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, LastId = g.Max(m => m.Id) })
            .ToListAsync(cancellationToken);

        if (latestIdsByGroup.Count == 0)
        {
            return Ok(Array.Empty<GroupDto>());
        }

        var latestIds = latestIdsByGroup.Select(x => x.LastId).ToList();
        var lastMessages = await dbContext.GroupMessages
            .Where(m => latestIds.Contains(m.Id))
            .Select(m => new { m.GroupId, m.MessageType, m.Text, m.EventTimestamp })
            .ToDictionaryAsync(m => m.GroupId, cancellationToken);

        var groupIds = latestIdsByGroup.Select(x => x.GroupId).ToList();

        var groupCache = await dbContext.Groups
            .AsNoTracking()
            .Where(g => groupIds.Contains(g.GroupId))
            .ToDictionaryAsync(g => g.GroupId, cancellationToken);

        var memberCounts = await dbContext.GroupMembers
            .Where(m => groupIds.Contains(m.GroupId))
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, cancellationToken);

        // 側欄預覽跟訊息串一樣要套遮蔽規則，不然關鍵字遮蔽會在側欄上被繞過
        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        var result = groupIds
            .Select(id =>
            {
                groupCache.TryGetValue(id, out var cached);
                var lastMessage = lastMessages[id];
                var preview = BuildPreview(lastMessage.MessageType, lastMessage.Text, maskingRules, id);

                return new GroupDto(
                    id,
                    cached?.GroupName ?? id,
                    cached?.PictureUrl,
                    preview,
                    lastMessage.EventTimestamp,
                    memberCounts.GetValueOrDefault(id, 0));
            })
            .OrderByDescending(g => g.LastMessageAt)
            .ToList();

        return Ok(result);
    }

    private static string BuildPreview(string messageType, string? text, IMaskingRuleSet maskingRules, string groupId)
    {
        var label = messageType switch
        {
            "text" => Truncate(maskingRules.MaskText(groupId, text ?? string.Empty)),
            "sticker" => "[貼圖]",
            "image" => "[圖片]",
            "video" => "[影片]",
            "audio" => "[語音訊息]",
            "file" => "[檔案]",
            _ => "[訊息]"
        };
        return label;
    }

    private static string Truncate(string text) =>
        text.Length <= PreviewMaxLength ? text : text[..PreviewMaxLength] + "…";
}
