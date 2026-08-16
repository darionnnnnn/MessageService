using MessageService.Data;
using MessageService.Services;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）
[ApiController]
[Route("api/users")]
[RequiresCapability(Capability.Viewer)]
public class UsersController(MessageDbContext dbContext, IMaskingService maskingService) : ControllerBase
{
    /// <summary>設定頁「自訂別名」的成員選單用。不帶 groupId 時回傳所有已知使用者（跨群組去重）。
    /// Anonymous 模式例外：全站其他地方（訊息串、搜尋、側欄）真實姓名一律不外流，這裡也不該是
    /// 繞過遮蔽的後門——回代號而非真名，且只讀已指派過的（不當場指派新的，指派只該發生在
    /// 使用者實際看到訊息時，見 IAnonymousIdentityService／訊息搜尋端點的姓名比對邏輯）。
    /// 其他模式（Original／MaskMiddle／CustomAlias）維持回真名——CustomAlias 模式的別名編輯器
    /// 本質上就需要看到真名才能設定別名，這是刻意的例外，不是遺漏。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupMemberDto>>> GetUsers(
        [FromQuery] string? groupId, CancellationToken cancellationToken)
    {
        var query = dbContext.GroupMembers.AsNoTracking();
        if (!string.IsNullOrEmpty(groupId))
        {
            query = query.Where(m => m.GroupId == groupId);
        }

        var members = await query
            .Select(m => new
            {
                m.GroupId,
                m.UserId,
                m.DisplayName,
                m.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        // 跨群組時同一人只留一筆代表列（挑最近更新的那筆），真名／代號都以這筆代表列的
        // GroupId 為準——代號是逐群組指派的，同一人在不同群組可能是不同代號
        var representatives = members
            .GroupBy(m => m.UserId)
            .Select(g => g.OrderByDescending(m => m.UpdatedAt).First())
            .ToList();

        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        List<GroupMemberDto> result;
        if (maskingRules.RequiresAnonymousIdentity)
        {
            var representativeGroupIds = representatives.Select(m => m.GroupId).Distinct().ToList();
            var identities = await dbContext.AnonymousIdentities
                .AsNoTracking()
                .Where(a => representativeGroupIds.Contains(a.GroupId))
                .ToDictionaryAsync(a => (a.GroupId, a.UserId), a => a.Label, cancellationToken);

            result = representatives
                .Where(m => identities.ContainsKey((m.GroupId, m.UserId)))
                .Select(m => new GroupMemberDto(m.UserId, identities[(m.GroupId, m.UserId)]))
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else
        {
            result = representatives
                .Select(m => new GroupMemberDto(m.UserId, m.DisplayName ?? m.UserId))
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return Ok(result);
    }
}
