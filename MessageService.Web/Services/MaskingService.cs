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

        return new MaskingRuleSet(settings?.NameDisplayMode ?? NameDisplayMode.MaskMiddle, keywords, aliases);
    }
}
