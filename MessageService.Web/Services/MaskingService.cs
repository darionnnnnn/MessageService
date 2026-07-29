using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class MaskingService(MessageDbContext dbContext) : IMaskingService
{
    public async Task<IMaskingRuleSet> LoadRulesAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ViewerSettings
            .FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId, cancellationToken);

        var keywords = await dbContext.MaskKeywords
            .Include(k => k.Groups)
            .ToListAsync(cancellationToken);

        var aliases = await dbContext.UserAliases
            .ToDictionaryAsync(a => a.UserId, a => a.Alias, cancellationToken);

        return new MaskingRuleSet(settings?.NameDisplayMode ?? NameDisplayMode.MaskMiddle, keywords, aliases);
    }
}
