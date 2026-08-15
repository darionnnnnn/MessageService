using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MessageService.Web.Services;

public class MaskingService(MessageDbContext dbContext, IMemoryCache cache) : IMaskingService
{
    private const string CacheKey = "masking-rules";

    public async Task<IMaskingRuleSet> LoadRulesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out IMaskingRuleSet? cached) && cached is not null)
        {
            return cached;
        }

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
        var rules = new MaskingRuleSet(
            effective.NameDisplayMode, keywords, aliases, PiiMaskingSettings.FromViewerSettings(effective));

        // 30 秒的取捨：拆機部署（Core／Edge 分開跑）時 InvalidateCache 只作用在本機程序，
        // 非寫入端最長 30 秒後才套用新規則，以此在記憶體查詢效能與跨行程一致性之間取得平衡。
        cache.Set(CacheKey, rules, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        return rules;
    }

    /// <summary>設定或規則被修改後呼叫，讓下一次 LoadRulesAsync 重新讀資料庫。</summary>
    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
    }
}
