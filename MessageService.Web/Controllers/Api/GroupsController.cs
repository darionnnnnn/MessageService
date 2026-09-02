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
public class GroupsController(
    MessageDbContext dbContext,
    IMaskingService maskingService,
    GroupDeletionService groupDeletionService) : ControllerBase
{
    // 側欄未讀數的上限：超過就一律顯示「99+」，也順便讓 COUNT 查詢在 SQL 端就截斷，
    // 不必真的數完一個很久沒看的群組累積的成千上萬則
    private const int UnreadCap = 100;

    private record LastMessagePreview(long Id, string MessageType, string? Text, DateTimeOffset EventTimestamp);

    // 這支不是側欄用的：不帶已讀基準，回傳的 UnreadCount 一律為 0。
    // 呼叫端是設定頁（wwwroot/js/settings.js，只要群組清單）、健康檢查與 IP 白名單相關測試。
    // 側欄一律走 POST /api/groups/list；誤用這支的症狀是「未讀數永遠 0」且從回應看不出原因。
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> GetGroups(CancellationToken cancellationToken)
    {
        return await BuildGroupListAsync(new Dictionary<string, long>(), cancellationToken);
    }

    // 側欄清單（帶已讀基準）。缺漏的 body（空內容）、Read 為 null、或空字典都當成「沒帶基準」處理，
    // 回 200 不是 400——側欄不該因為基準缺漏就整個掛掉。
    // 這個寬容只到「缺漏」為止：Read 的值型別不對（例如 {"read":{"G1":"abc"}}）時，[ApiController]
    // 的模型繫結會在進到這裡之前就回 400（實測確認）。舊的 ?read= 字串版本是自己解析、壞掉的 pair
    // 直接略過，所以型別錯誤也回 200；改成 JSON 後這條行為變了，但唯一的呼叫端是自家前端
    // （送的一定是數字），不值得為此關掉模型繫結的驗證。
    [HttpPost("list")]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> ListGroups(
        [FromBody] GroupListRequestDto? request, CancellationToken cancellationToken)
    {
        return await BuildGroupListAsync(request?.Read ?? new Dictionary<string, long>(), cancellationToken);
    }

    /// <summary>刪除指定群組的全部歷史訊息，保留群組本體與快取。</summary>
    [HttpDelete("{groupId}/messages")]
    public async Task<ActionResult<GroupDeletionResultDto>> DeleteMessages(
        string groupId, CancellationToken cancellationToken)
    {
        var result = await groupDeletionService.DeleteMessagesAsync(groupId, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(new GroupDeletionResultDto(result.MessageCount, 0, 0, 0, 0));
    }

    /// <summary>刪除指定群組本體及其所有相關資料。</summary>
    [HttpDelete("{groupId}")]
    public async Task<ActionResult<GroupDeletionResultDto>> DeleteGroup(
        string groupId, CancellationToken cancellationToken)
    {
        var result = await groupDeletionService.DeleteGroupAsync(groupId, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(new GroupDeletionResultDto(
            result.MessageCount, result.MemberCount, result.AnonymousIdentityCount,
            result.MaskKeywordScopeCount, result.HighlightScopeCount));
    }

    private async Task<ActionResult<IReadOnlyList<GroupDto>>> BuildGroupListAsync(
        IReadOnlyDictionary<string, long> readBaselines, CancellationToken cancellationToken)
    {
        // 側欄清單改讀 Groups.LastMessageId／LastMessageAt（由 GroupLastMessageTracker 在訊息
        // 落地時維護），不再對 GroupMessages 全表做 GroupBy+Max——Groups 表只有幾十列，
        // 這裡永遠是一次小查詢，跟訊息量無關。只在有訊息的群組才會被列出（跟舊行為一致）。
        var groups = await dbContext.Groups
            .AsNoTracking()
            .Where(g => g.LastMessageId != null)
            .Select(g => new { g.GroupId, g.GroupName, HasPicture = g.Picture != null, LastMessageId = g.LastMessageId!.Value })
            .ToListAsync(cancellationToken);

        if (groups.Count == 0)
        {
            return Ok(Array.Empty<GroupDto>());
        }

        // 已讀基準：每個瀏覽器自己記在 localStorage，由 POST body 帶上來。
        // 未讀數＝該群組 Id 大於基準的訊息數（上限 UnreadCap）。沒帶基準的群組視為全部已讀（0），
        // 避免第一次開啟就整排 99+。只對「有 baseline 且確實有新訊息」的群組才查——
        // N+1 的 N 從「所有有訊息的群組」縮成「真的有未讀的群組」，且每查都走 (GroupId, Id) 索引
        var unreadByGroup = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            if (!readBaselines.TryGetValue(g.GroupId, out var baseline) || g.LastMessageId <= baseline)
            {
                continue;
            }
            unreadByGroup[g.GroupId] = await dbContext.GroupMessages
                .Where(m => m.GroupId == g.GroupId && m.Id > baseline)
                .Take(UnreadCap)
                .CountAsync(cancellationToken);
        }

        // 最後訊息預覽：批次用 Id IN(...) 撈——latestIds 天生每群組只有一個值，用 GroupId
        // 當 dictionary key 不會撞。先投影成匿名型別讓 EF 轉譯 SQL，記錄型別的建構留在
        // 記憶體裡組，避免自訂建構子在 Select() 內轉譯失敗的風險
        var latestIds = groups.Select(g => g.LastMessageId).ToList();
        var lastMessageRows = await dbContext.GroupMessages
            .Where(m => latestIds.Contains(m.Id))
            .Select(m => new { m.GroupId, m.Id, m.MessageType, m.Text, m.EventTimestamp })
            .ToListAsync(cancellationToken);
        var lastMessages = lastMessageRows.ToDictionary(
            m => m.GroupId, m => new LastMessagePreview(m.Id, m.MessageType, m.Text, m.EventTimestamp));

        var groupIds = groups.Select(g => g.GroupId).ToList();
        var memberCounts = await dbContext.GroupMembers
            .Where(m => groupIds.Contains(m.GroupId))
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, cancellationToken);

        // 側欄預覽跟訊息串一樣要套遮蔽規則，不然關鍵字遮蔽會在側欄上被繞過
        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        var result = new List<GroupDto>();
        foreach (var g in groups)
        {
            if (!lastMessages.TryGetValue(g.GroupId, out var lastMessage))
            {
                // Groups.LastMessageId 指向的那一列剛好被保留期清除刪掉了（罕見但可能，
                // 兩段查詢之間有空檔）——退回即時查一次這個群組目前真正的最後一則，順便修正
                // Groups 列，下一輪 GetGroups 就不用再重跑這段偵測與回退
                var recovered = await RecoverDriftedLastMessageAsync(g.GroupId, cancellationToken);
                if (recovered is null)
                {
                    continue; // 這個群組的訊息全被清空了，這輪不顯示（下次有新訊息會重新出現）
                }
                lastMessage = recovered;
            }

            var preview = MessagePreviewFormatter.Format(lastMessage.MessageType, lastMessage.Text, maskingRules, g.GroupId);

            result.Add(new GroupDto(
                g.GroupId,
                g.GroupName ?? g.GroupId,
                g.HasPicture ? $"api/groups/{g.GroupId}/avatar" : null,
                preview,
                lastMessage.EventTimestamp,
                memberCounts.GetValueOrDefault(g.GroupId, 0),
                lastMessage.Id,
                unreadByGroup.GetValueOrDefault(g.GroupId, 0)));
        }

        return Ok(result.OrderByDescending(g => g.LastMessageAt).ToList());
    }

    /// <summary>Groups.LastMessageId 跟 GroupMessages 實際內容漂移時的回退：即時查一次該群組
    /// 目前真正的最後一則並修正 Groups 列。回傳 null 代表這個群組的訊息已經全被清空。</summary>
    private async Task<LastMessagePreview?> RecoverDriftedLastMessageAsync(string groupId, CancellationToken cancellationToken)
    {
        var actualRow = await dbContext.GroupMessages
            .Where(m => m.GroupId == groupId)
            .OrderByDescending(m => m.Id)
            .Select(m => new { m.Id, m.MessageType, m.Text, m.EventTimestamp })
            .FirstOrDefaultAsync(cancellationToken);
        var actual = actualRow is null
            ? null
            : new LastMessagePreview(actualRow.Id, actualRow.MessageType, actualRow.Text, actualRow.EventTimestamp);

        var group = await dbContext.Groups.FindAsync([groupId], cancellationToken);
        if (actual is null)
        {
            if (group is not null)
            {
                group.LastMessageId = null;
                group.LastMessageAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return null;
        }

        if (group is not null)
        {
            group.LastMessageId = actual.Id;
            group.LastMessageAt = actual.EventTimestamp;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return actual;
    }
}
