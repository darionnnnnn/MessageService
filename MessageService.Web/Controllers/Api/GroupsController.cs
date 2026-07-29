using MessageService.Data;
using MessageService.Web.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

[ApiController]
[Route("api/groups")]
public class GroupsController(MessageDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> GetGroups(CancellationToken cancellationToken)
    {
        var groupIds = await dbContext.GroupMessages
            .Select(m => m.GroupId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var groupCache = await dbContext.Groups
            .Where(g => groupIds.Contains(g.GroupId))
            .ToDictionaryAsync(g => g.GroupId, cancellationToken);

        var result = groupIds
            .Select(id =>
            {
                groupCache.TryGetValue(id, out var cached);
                return new GroupDto(id, cached?.GroupName ?? id, cached?.PictureUrl);
            })
            .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(result);
    }
}
