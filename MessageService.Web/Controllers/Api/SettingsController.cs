using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Controllers.Api;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）
[ApiController]
[Route("api/settings")]
[RequiresCapability(Capability.Viewer)]
public class SettingsController(
    MessageDbContext dbContext, IOptions<HeartbeatOptions> heartbeatOptions, DatabaseStartupDecision databaseStartupDecision)
    : ControllerBase
{
    // 只有本機這台主機的救場狀態（見 DatabaseStartupDecision 的單例說明：只在啟動時決定一次，
    // 行程存續期間不變）——不是跨主機彙整，AllInOne 以外的模式一律回報「沒有觸發」
    [HttpGet("database-status")]
    public ActionResult<DatabaseStatusDto> GetDatabaseStatus() =>
        Ok(new DatabaseStatusDto(
            databaseStartupDecision.EffectiveProvider,
            databaseStartupDecision.SqliteFallbackTriggered,
            databaseStartupDecision.SqliteFallbackReason));


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

    [HttpGet("retention")]
    public async Task<ActionResult<RetentionSettingsDto>> GetRetentionSettings(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        return Ok(new RetentionSettingsDto(settings?.RetentionDays ?? ViewerSettings.DefaultRetentionDays));
    }

    /// <summary>不可逆：改小這個值之後，下次每日清除排程一到就會永久刪除超過這個天數的訊息，
    /// 沒有復原機制。前端要在送出前跟使用者二次確認，見 settings.js。</summary>
    [HttpPut("retention")]
    public async Task<IActionResult> UpdateRetentionSettings(
        [FromBody] RetentionSettingsDto dto, CancellationToken cancellationToken)
    {
        // 用 ViewerSettings.MaxRetentionDays 而不是借 MessagesController.MaxDays——後者的語意是
        // 「查詢視窗最大天數」，跟保留期上限只是剛好同值，將來要能各自調整
        if (dto.RetentionDays is < 1 or > ViewerSettings.MaxRetentionDays)
        {
            return BadRequest($"RetentionDays must be between 1 and {ViewerSettings.MaxRetentionDays}.");
        }

        var settings = await dbContext.ViewerSettings
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            dbContext.ViewerSettings.Add(new ViewerSettings { RetentionDays = dto.RetentionDays });
        }
        else
        {
            settings.RetentionDays = dto.RetentionDays;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("pii-masking")]
    public async Task<ActionResult<PiiMaskingSettingsDto>> GetPiiMaskingSettings(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        return Ok(settings is null
            ? new PiiMaskingSettingsDto(true, true, true, true)
            : new PiiMaskingSettingsDto(settings.MaskNationalId, settings.MaskMobilePhone, settings.MaskLandline, settings.MaskNhiCard));
    }

    [HttpPut("pii-masking")]
    public async Task<IActionResult> UpdatePiiMaskingSettings(
        [FromBody] PiiMaskingSettingsDto dto, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            dbContext.ViewerSettings.Add(new ViewerSettings
            {
                MaskNationalId = dto.MaskNationalId,
                MaskMobilePhone = dto.MaskMobilePhone,
                MaskLandline = dto.MaskLandline,
                MaskNhiCard = dto.MaskNhiCard
            });
        }
        else
        {
            settings.MaskNationalId = dto.MaskNationalId;
            settings.MaskMobilePhone = dto.MaskMobilePhone;
            settings.MaskLandline = dto.MaskLandline;
            settings.MaskNhiCard = dto.MaskNhiCard;
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

    // === 主機狀態（需求4：Web 端要能看到另外幾台服務是否正常運作，見
    // docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次D）===

    [HttpGet("host-heartbeats")]
    public async Task<ActionResult<IReadOnlyList<HostHeartbeatDto>>> GetHostHeartbeats(CancellationToken cancellationToken)
    {
        var rows = await dbContext.HostHeartbeats
            .AsNoTracking()
            .OrderBy(h => h.Role).ThenBy(h => h.MachineName)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromSeconds(Math.Max(1, heartbeatOptions.Value.IntervalSeconds));

        return Ok(rows.Select(h => new HostHeartbeatDto(
            h.Role, h.MachineName, h.LastSeenAt, ComputeStatus(now - h.LastSeenAt, interval),
            h.OutboxPending, h.OutboxOldestAgeSeconds, h.EncryptionKeyFingerprint)).ToList());
    }

    // 主機更名、角色改了、或那台機器退役時，舊列會永遠留著顯示 Offline，而且原本沒有任何
    // 刪除入口——不做自動清除（自動清除會在真的離線時把「離線的主機」從畫面上抹掉，剛好抹掉
    // 使用者要看的那件事），改成手動移除，前端二次確認
    [HttpDelete("host-heartbeats/{role}/{machineName}")]
    public async Task<IActionResult> DeleteHostHeartbeat(string role, string machineName, CancellationToken cancellationToken)
    {
        var existing = await dbContext.HostHeartbeats.FindAsync([role, machineName], cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        dbContext.HostHeartbeats.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string ComputeStatus(TimeSpan age, TimeSpan interval)
    {
        if (age < interval * 2)
        {
            return "Online";
        }
        return age < interval * 5 ? "Delayed" : "Offline";
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
