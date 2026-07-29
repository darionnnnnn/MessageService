using MessageService.Data;
using MessageService.Web.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

[ApiController]
[Route("api/users")]
public class UsersController(MessageDbContext dbContext) : ControllerBase
{
    /// <summary>設定頁「自訂別名」的成員選單用。不帶 groupId 時回傳所有已知使用者（跨群組去重）。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupMemberDto>>> GetUsers(
        [FromQuery] string? groupId, CancellationToken cancellationToken)
    {
        var query = dbContext.GroupMembers.AsNoTracking();
        if (!string.IsNullOrEmpty(groupId))
        {
            query = query.Where(m => m.GroupId == groupId);
        }

        var members = await query.ToListAsync(cancellationToken);

        var result = members
            .GroupBy(m => m.UserId)
            .Select(g => g.OrderByDescending(m => m.UpdatedAt).First())
            .Select(m => new GroupMemberDto(m.UserId, m.DisplayName ?? m.UserId))
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(result);
    }
}
