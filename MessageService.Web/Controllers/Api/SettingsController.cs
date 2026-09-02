using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Controllers.Api;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）
[ApiController]
[Route("api/settings")]
[RequiresCapability(Capability.Viewer)]
public class SettingsController(
    MessageDbContext dbContext,
    IMaskingService maskingService,
    IOptions<HeartbeatOptions> heartbeatOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    DatabaseStartupDecision databaseStartupDecision)
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
        maskingService.InvalidateCache();
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

        // singleton 列不存在時退回類別預設（跟 migration 種子同一個來源），不要在這裡另外硬寫
        // 一份布林值——健保卡預設改關那次，這裡硬寫的 (true,true,true,true) 就跟真正的預設漂移了
        var effective = settings ?? new ViewerSettings();
        return Ok(new PiiMaskingSettingsDto(
            effective.MaskNationalId, effective.MaskMobilePhone, effective.MaskLandline, effective.MaskNhiCard));
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
        maskingService.InvalidateCache();
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
        maskingService.InvalidateCache();

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
        maskingService.InvalidateCache();
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
        maskingService.InvalidateCache();
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
        maskingService.InvalidateCache();
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
        maskingService.InvalidateCache();
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
            h.OutboxPending, h.OutboxOldestAgeSeconds, h.EncryptionKeyFingerprint, h.Channel)).ToList());
    }

    [HttpGet("message-flow")]
    public async Task<ActionResult<MessageFlowDto>> GetMessageFlow(CancellationToken cancellationToken)
    {
        var maxLastMessageAt = await dbContext.Groups
            .AsNoTracking()
            .Select(g => g.LastMessageAt)
            .MaxAsync(cancellationToken);

        string status;
        if (maxLastMessageAt is null)
        {
            status = "None";
        }
        else
        {
            var warnHours = monitoringOptions.Value.MessageSilenceWarnHours;
            if (warnHours <= 0 || DateTimeOffset.UtcNow - maxLastMessageAt.Value <= TimeSpan.FromHours(warnHours))
            {
                status = "Ok";
            }
            else
            {
                status = "Silent";
            }
        }

        return Ok(new MessageFlowDto(maxLastMessageAt, status));
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

    // === 訊息高亮（作業 B-1）===

    [HttpGet("highlight-keywords")]
    public async Task<ActionResult<List<HighlightKeywordDto>>> GetHighlightKeywords(CancellationToken cancellationToken)
    {
        var keywords = await LoadHighlightKeywordDtosAsync(cancellationToken);
        return Ok(keywords);
    }

    [HttpPost("highlight-keywords")]
    public async Task<ActionResult<HighlightKeywordDto>> CreateHighlightKeyword(
        [FromBody] UpsertHighlightKeywordDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Keyword))
        {
            return BadRequest("Keyword is required.");
        }

        var keyword = new HighlightKeyword
        {
            Keyword = dto.Keyword,
            ApplyToAllGroups = dto.ApplyToAllGroups
        };
        ApplyHighlightGroupSelection(keyword, dto);

        dbContext.HighlightKeywords.Add(keyword);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(ToHighlightKeywordDto(keyword));
    }

    [HttpPut("highlight-keywords/{id:int}")]
    public async Task<ActionResult<HighlightKeywordDto>> UpdateHighlightKeyword(
        int id, [FromBody] UpsertHighlightKeywordDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Keyword))
        {
            return BadRequest("Keyword is required.");
        }

        var keyword = await dbContext.HighlightKeywords
            .Include(k => k.Groups)
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

        if (keyword is null)
        {
            return NotFound();
        }

        keyword.Keyword = dto.Keyword;
        keyword.ApplyToAllGroups = dto.ApplyToAllGroups;
        dbContext.HighlightKeywordGroups.RemoveRange(keyword.Groups);
        keyword.Groups.Clear();
        ApplyHighlightGroupSelection(keyword, dto);

        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToHighlightKeywordDto(keyword));
    }

    [HttpDelete("highlight-keywords/{id:int}")]
    public async Task<IActionResult> DeleteHighlightKeyword(int id, CancellationToken cancellationToken)
    {
        var keyword = await dbContext.HighlightKeywords.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (keyword is null)
        {
            return NotFound();
        }

        dbContext.HighlightKeywords.Remove(keyword);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("highlight-users")]
    public async Task<ActionResult<List<HighlightUserDto>>> GetHighlightUsers(CancellationToken cancellationToken)
    {
        var users = await LoadHighlightUserDtosAsync(cancellationToken);
        return Ok(users);
    }

    [HttpPost("highlight-users")]
    public async Task<ActionResult<HighlightUserDto>> CreateHighlightUser(
        [FromBody] UpsertHighlightUserDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.UserId))
        {
            return BadRequest("UserId is required.");
        }

        var existing = await dbContext.HighlightUsers
            .FirstOrDefaultAsync(
                u => u.UserId == dto.UserId && (dto.GroupId == null ? u.GroupId == null : u.GroupId == dto.GroupId),
                cancellationToken);

        if (existing is not null)
        {
            var existingDto = await ResolveHighlightUserDtoAsync(existing, cancellationToken);
            return Ok(existingDto);
        }

        var user = new HighlightUser
        {
            UserId = dto.UserId,
            GroupId = dto.GroupId
        };
        dbContext.HighlightUsers.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        var createdDto = await ResolveHighlightUserDtoAsync(user, cancellationToken);
        return Ok(createdDto);
    }

    [HttpDelete("highlight-users/{id:int}")]
    public async Task<IActionResult> DeleteHighlightUser(int id, CancellationToken cancellationToken)
    {
        var user = await dbContext.HighlightUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        dbContext.HighlightUsers.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("highlight-rules")]
    public async Task<ActionResult<HighlightRulesDto>> GetHighlightRules(CancellationToken cancellationToken)
    {
        var keywords = await LoadHighlightKeywordDtosAsync(cancellationToken);
        var users = await LoadHighlightUserDtosAsync(cancellationToken);
        return Ok(new HighlightRulesDto(keywords, users));
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

    private async Task<List<HighlightKeywordDto>> LoadHighlightKeywordDtosAsync(CancellationToken cancellationToken)
    {
        var keywords = await dbContext.HighlightKeywords
            .AsNoTracking()
            .Include(k => k.Groups)
            .OrderBy(k => k.Id)
            .ToListAsync(cancellationToken);

        return keywords.Select(ToHighlightKeywordDto).ToList();
    }

    private static void ApplyHighlightGroupSelection(HighlightKeyword keyword, UpsertHighlightKeywordDto dto)
    {
        if (dto.ApplyToAllGroups || dto.GroupIds is null)
        {
            return;
        }

        foreach (var groupId in dto.GroupIds.Distinct())
        {
            keyword.Groups.Add(new HighlightKeywordGroup { GroupId = groupId });
        }
    }

    private static HighlightKeywordDto ToHighlightKeywordDto(HighlightKeyword keyword) => new(
        keyword.Id,
        keyword.Keyword,
        keyword.ApplyToAllGroups,
        keyword.Groups.Select(g => g.GroupId).ToList());

    private async Task<List<HighlightUserDto>> LoadHighlightUserDtosAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.HighlightUsers
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken);

        if (users.Count == 0)
        {
            return [];
        }

        var userIds = users.Select(u => u.UserId).Distinct().ToList();
        var groupIds = users.Where(u => u.GroupId != null).Select(u => u.GroupId!).Distinct().ToList();

        Dictionary<string, string> groupNames = [];
        if (groupIds.Count > 0)
        {
            groupNames = await dbContext.Groups
                .AsNoTracking()
                .Where(g => groupIds.Contains(g.GroupId))
                .ToDictionaryAsync(g => g.GroupId, g => g.GroupName ?? g.GroupId, cancellationToken);
        }

        var members = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => userIds.Contains(m.UserId))
            .Select(m => new
            {
                m.GroupId,
                m.UserId,
                m.DisplayName,
                m.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        Dictionary<(string GroupId, string UserId), string> identities = [];
        if (maskingRules.RequiresAnonymousIdentity)
        {
            var relevantGroupIds = members.Select(m => m.GroupId).Distinct().ToList();
            if (relevantGroupIds.Count > 0)
            {
                identities = await dbContext.AnonymousIdentities
                    .AsNoTracking()
                    .Where(a => relevantGroupIds.Contains(a.GroupId) && userIds.Contains(a.UserId))
                    .ToDictionaryAsync(a => (a.GroupId, a.UserId), a => a.Label, cancellationToken);
            }
        }

        var result = new List<HighlightUserDto>(users.Count);
        foreach (var user in users)
        {
            string? groupName = null;
            if (user.GroupId != null)
            {
                groupName = groupNames.TryGetValue(user.GroupId, out var name) ? name : user.GroupId;
            }

            var userMembers = members.Where(m => m.UserId == user.UserId).ToList();
            var targetMember = user.GroupId != null
                ? (userMembers.FirstOrDefault(m => m.GroupId == user.GroupId)
                   ?? userMembers.OrderByDescending(m => m.UpdatedAt).FirstOrDefault())
                : userMembers.OrderByDescending(m => m.UpdatedAt).FirstOrDefault();

            string displayName;
            if (maskingRules.RequiresAnonymousIdentity)
            {
                if (targetMember != null && identities.TryGetValue((targetMember.GroupId, user.UserId), out var label))
                {
                    displayName = label;
                }
                else
                {
                    displayName = user.UserId;
                }
            }
            else
            {
                displayName = targetMember?.DisplayName ?? user.UserId;
            }

            result.Add(new HighlightUserDto(user.Id, user.UserId, user.GroupId, displayName, groupName));
        }

        return result;
    }

    /// <summary>單筆高亮人員的 DTO 解析。名稱與群組名的解析規則只在
    /// <see cref="LoadHighlightUserDtosAsync"/> 寫一份，這裡直接複用後挑出目標那一筆——
    /// HighlightUsers 是使用者手動維護的小表，整批載入的成本遠低於維護兩份會漂移的解析邏輯。</summary>
    private async Task<HighlightUserDto> ResolveHighlightUserDtoAsync(HighlightUser user, CancellationToken cancellationToken)
    {
        var all = await LoadHighlightUserDtosAsync(cancellationToken);
        return all.First(u => u.Id == user.Id);
    }
}
