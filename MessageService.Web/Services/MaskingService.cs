using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class MaskingService(MessageDbContext dbContext) : IMaskingService
{
    public async Task<IMaskingRuleSet> LoadRulesAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        var keywords = await dbContext.MaskKeywords
            .AsNoTracking()
            .Include(k => k.Groups)
            .ToListAsync(cancellationToken);

        var aliases = await dbContext.UserAliases
            .AsNoTracking()
            .ToDictionaryAsync(a => a.UserId, a => a.Alias, cancellationToken);

        // singleton 設定列不存在時退回類別預設（跟 migration 種子同一個來源）——之前這裡硬寫
        // AllEnabled，健保卡預設改關之後就跟真正的預設漂移了；統一走 PiiMaskingSettings 的
        // 投影，讓「類別預設值」永遠只有一個定義點
        var effective = settings ?? new ViewerSettings();
        return new MaskingRuleSet(
            effective.NameDisplayMode, keywords, aliases, PiiMaskingSettings.FromViewerSettings(effective));
    }
}
