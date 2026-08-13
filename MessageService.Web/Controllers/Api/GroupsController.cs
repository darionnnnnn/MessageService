using MessageService.Data;
using MessageService.Services;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）
[ApiController]
[Route("api/groups")]
[RequiresCapability(Capability.Viewer)]
public class GroupsController(MessageDbContext dbContext, IMaskingService maskingService) : ControllerBase
{
    // 側欄未讀數的上限：超過就一律顯示「99+」，也順便讓 COUNT 查詢在 SQL 端就截斷，
    // 不必真的數完一個很久沒看的群組累積的成千上萬則
    private const int UnreadCap = 100;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> GetGroups(
        [FromQuery] string? read, CancellationToken cancellationToken)
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

        // 已讀基準：每個瀏覽器自己記在 localStorage，用 ?read=群組:最後已讀Id,... 帶上來。
        // 未讀數＝該群組 Id 大於基準的訊息數（上限 UnreadCap）。沒帶基準的群組視為全部已讀（0），
        // 避免第一次開啟就整排 99+
        var readBaselines = ParseReadBaselines(read);
        var unreadByGroup = new Dictionary<string, int>();
        foreach (var entry in latestIdsByGroup)
        {
            if (!readBaselines.TryGetValue(entry.GroupId, out var baseline) || entry.LastId <= baseline)
            {
                continue;
            }
            unreadByGroup[entry.GroupId] = await dbContext.GroupMessages
                .Where(m => m.GroupId == entry.GroupId && m.Id > baseline)
                .Take(UnreadCap)
                .CountAsync(cancellationToken);
        }

        var lastIdByGroup = latestIdsByGroup.ToDictionary(x => x.GroupId, x => x.LastId);

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
                // 兩段查詢之間若剛好被保留期清除刪掉那則訊息（罕見但可能），這個群組這輪就跳過，
                // 不讓整支側欄 API 500——下一輪輪詢會用當時仍存在的最後一則訊息重新算出結果
                if (!lastMessages.TryGetValue(id, out var lastMessage))
                {
                    return null;
                }

                groupCache.TryGetValue(id, out var cached);
                var preview = MessagePreviewFormatter.Format(lastMessage.MessageType, lastMessage.Text, maskingRules, id);

                return new GroupDto(
                    id,
                    cached?.GroupName ?? id,
                    cached?.PictureUrl,
                    preview,
                    lastMessage.EventTimestamp,
                    memberCounts.GetValueOrDefault(id, 0),
                    lastIdByGroup[id],
                    unreadByGroup.GetValueOrDefault(id, 0));
            })
            .Where(g => g is not null)
            .OrderByDescending(g => g!.LastMessageAt)
            .ToList();

        return Ok(result);
    }

    // 解析 ?read=群組:最後已讀Id,群組:最後已讀Id... 成對照表；格式不合的 pair 直接略過，
    // 不讓一個壞掉的查詢字串整個弄垮側欄
    private static Dictionary<string, long> ParseReadBaselines(string? read)
    {
        var baselines = new Dictionary<string, long>();
        if (string.IsNullOrWhiteSpace(read))
        {
            return baselines;
        }

        foreach (var pair in read.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.LastIndexOf(':');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }
            var groupId = pair[..separator];
            if (long.TryParse(pair[(separator + 1)..], out var lastReadId))
            {
                baselines[groupId] = lastReadId;
            }
        }

        return baselines;
    }

}
