using MessageService.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>背景補刷服務：目前唯一的刷新觸發是新訊息落地（IngestSideEffects），
/// 安靜的群組若當初沒抓成功或換了頭貼未發言，快取就可能永遠停在原始 Id 與代號圖示。
/// 此服務定期透過 IProfileStore 挑出已過期或缺圖的群組與成員，將候選項目丟進佇列重新刷新。</summary>
public class ProfileBackfillService(
    IServiceScopeFactory scopeFactory,
    IProfileRefreshQueue queue,
    IOptions<ProfileCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<ProfileBackfillService> logger) : BackgroundService
{
    private readonly ProfileCacheOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.BackfillIntervalMinutes));
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunBackfillAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Profile backfill scan failed; will retry at next scheduled run");
            }
        }
    }

    public async Task RunBackfillAsync(CancellationToken cancellationToken)
    {
        var maxPerScan = _options.BackfillMaxPerScan;
        if (maxPerScan <= 0)
        {
            return;
        }

        var cutoff = timeProvider.GetUtcNow() - _options.RefreshAfter;

        using var scope = scopeFactory.CreateScope();
        var profileStore = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        var tasks = await profileStore.GetStaleProfilesAsync(maxPerScan, cutoff, cancellationToken);

        var groupCount = 0;
        var memberCount = 0;

        foreach (var task in tasks)
        {
            if (task.UserId is null)
            {
                groupCount++;
            }
            else
            {
                memberCount++;
            }
            queue.Enqueue(task);
        }

        logger.LogInformation(
            "Profile backfill enqueued {GroupCount} group(s) and {MemberCount} member(s).",
            groupCount, memberCount);
    }
}
