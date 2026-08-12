using MessageService.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>資料存取全部透過 IProfileStore（Full／Db 模式查本機 DB，Line 模式打 ingest API）——
/// 這裡只保留「查 TTL → 打 LINE → 過期才 upsert」的流程本身，不直接碰任何資料庫或 HTTP client。</summary>
public class ProfileRefreshService(
    IProfileRefreshQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ProfileCacheOptions> options,
    ILogger<ProfileRefreshService> logger) : BackgroundService
{
    private readonly ProfileCacheOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var task in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(task, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error refreshing profile cache for group {GroupId} user {UserId}",
                    task.GroupId, task.UserId);
            }
        }
    }

    public async Task ProcessAsync(ProfileRefreshTask task, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var profileStore = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        var profileClient = scope.ServiceProvider.GetRequiredService<ILineProfileClient>();
        var cutoff = DateTimeOffset.UtcNow - _options.RefreshAfter;

        // 一次查完群組與成員的 staleness——TTL 判斷一定要在打 LINE API 之前完成才省得到配額，
        // 這也是 IProfileStore 把 staleness 跟 upsert 拆成兩支方法的理由
        var staleness = await profileStore.GetStalenessAsync(task.GroupId, task.UserId, cutoff, cancellationToken);

        if (staleness.GroupStale)
        {
            await RefreshGroupAsync(profileStore, profileClient, task.GroupId, cancellationToken);
        }

        if (task.UserId is not null && staleness.MemberStale)
        {
            await RefreshMemberAsync(profileStore, profileClient, task.GroupId, task.UserId, cancellationToken);
        }
    }

    private async Task RefreshGroupAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, CancellationToken cancellationToken)
    {
        var summary = await profileClient.GetGroupSummaryAsync(groupId, cancellationToken);
        if (summary is null)
        {
            logger.LogWarning("Group summary unavailable for group {GroupId}", groupId);
            return;
        }

        await profileStore.UpsertGroupAsync(groupId, summary, cancellationToken);
    }

    private async Task RefreshMemberAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string userId, CancellationToken cancellationToken)
    {
        var profile = await profileClient.GetGroupMemberProfileAsync(groupId, userId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("Member profile unavailable for group {GroupId} user {UserId}", groupId, userId);
            return;
        }

        await profileStore.UpsertMemberAsync(groupId, userId, profile, cancellationToken);
    }
}
