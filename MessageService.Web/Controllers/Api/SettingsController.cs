using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Controllers.Api;

[ApiController]
[Route("api/settings")]
public class SettingsController(MessageDbContext dbContext) : ControllerBase
{
    [HttpGet("display")]
    public async Task<ActionResult<DisplaySettingsDto>> GetDisplaySettings(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        return Ok(new DisplaySettingsDto((settings?.NameDisplayMode ?? NameDisplayMode.MaskMiddle).ToString()));
    }

    [HttpPut("display")]
    public async Task<IActionResult> UpdateDisplaySettings(
        [FromBody] DisplaySettingsDto dto, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<NameDisplayMode>(dto.NameDisplayMode, out var mode))
        {
            return BadRequest($"Unknown NameDisplayMode: {dto.NameDisplayMode}");
        }

        var settings = await dbContext.ViewerSettings
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            dbContext.ViewerSettings.Add(new ViewerSettings { NameDisplayMode = mode });
        }
        else
        {
            settings.NameDisplayMode = mode;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("keywords")]
    public async Task<ActionResult<IReadOnlyList<MaskKeywordDto>>> GetKeywords(CancellationToken cancellationToken)
    {
        var keywords = await dbContext.MaskKeywords
            .AsNoTracking()
            .Include(k => k.Groups)
            .OrderBy(k => k.Id)
            .ToListAsync(cancellationToken);

        return Ok(keywords.Select(ToDto).ToList());
    }

    [HttpPost("keywords")]
    public async Task<ActionResult<MaskKeywordDto>> CreateKeyword(
        [FromBody] UpsertMaskKeywordDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Keyword))
        {
            return BadRequest("Keyword is required.");
        }

        var keyword = new MaskKeyword
        {
            Keyword = dto.Keyword,
            Replacement = string.IsNullOrWhiteSpace(dto.Replacement) ? null : dto.Replacement,
            ApplyToAllGroups = dto.ApplyToAllGroups
        };
        ApplyGroupSelection(keyword, dto);

        dbContext.MaskKeywords.Add(keyword);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(ToDto(keyword));
    }

    [HttpPut("keywords/{id:int}")]
    public async Task<IActionResult> UpdateKeyword(
        int id, [FromBody] UpsertMaskKeywordDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Keyword))
        {
            return BadRequest("Keyword is required.");
        }

        var keyword = await dbContext.MaskKeywords
            .Include(k => k.Groups)
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

        if (keyword is null)
        {
            return NotFound();
        }

        keyword.Keyword = dto.Keyword;
        keyword.Replacement = string.IsNullOrWhiteSpace(dto.Replacement) ? null : dto.Replacement;
        keyword.ApplyToAllGroups = dto.ApplyToAllGroups;
        dbContext.MaskKeywordGroups.RemoveRange(keyword.Groups);
        keyword.Groups.Clear();
        ApplyGroupSelection(keyword, dto);

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("keywords/{id:int}")]
    public async Task<IActionResult> DeleteKeyword(int id, CancellationToken cancellationToken)
    {
        var keyword = await dbContext.MaskKeywords.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (keyword is null)
        {
            return NotFound();
        }

        dbContext.MaskKeywords.Remove(keyword);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("aliases")]
    public async Task<ActionResult<IReadOnlyList<UserAliasDto>>> GetAliases(CancellationToken cancellationToken)
    {
        var aliases = await dbContext.UserAliases
            .OrderBy(a => a.UserId)
            .Select(a => new UserAliasDto(a.UserId, a.Alias))
            .ToListAsync(cancellationToken);

        return Ok(aliases);
    }

    [HttpPut("aliases/{userId}")]
    public async Task<IActionResult> UpsertAlias(
        string userId, [FromBody] UpsertUserAliasDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Alias))
        {
            return BadRequest("Alias is required.");
        }

        var existing = await dbContext.UserAliases.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (existing is null)
        {
            dbContext.UserAliases.Add(new UserAlias { UserId = userId, Alias = dto.Alias });
        }
        else
        {
            existing.Alias = dto.Alias;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("aliases/{userId}")]
    public async Task<IActionResult> DeleteAlias(string userId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.UserAliases.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        dbContext.UserAliases.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static void ApplyGroupSelection(MaskKeyword keyword, UpsertMaskKeywordDto dto)
    {
        if (dto.ApplyToAllGroups || dto.GroupIds is null)
        {
            return;
        }

        foreach (var groupId in dto.GroupIds.Distinct())
        {
            keyword.Groups.Add(new MaskKeywordGroup { GroupId = groupId });
        }
    }

    private static MaskKeywordDto ToDto(MaskKeyword keyword) => new(
        keyword.Id, keyword.Keyword, keyword.Replacement, keyword.ApplyToAllGroups,
        keyword.Groups.Select(g => g.GroupId).ToList());
}
