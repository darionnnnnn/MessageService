using System.Collections.Concurrent;
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

    /// <summary>程序內失敗冷卻：group 用 GroupId 當鍵、member 用 "GroupId:UserId"，見
    /// ProfileCacheOptions.FailureRetryAfter 的說明。單例 BackgroundService 的欄位，
    /// 整個服務生命週期共用一份，重啟就重置。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failureCooldowns = new();

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

        if (staleness.GroupStale && !IsInCooldown(task.GroupId))
        {
            await RefreshGroupAsync(profileStore, profileClient, task.GroupId, cancellationToken);
        }

        if (task.UserId is not null && staleness.MemberStale && !IsInCooldown(MemberCooldownKey(task.GroupId, task.UserId)))
        {
            await RefreshMemberAsync(profileStore, profileClient, task.GroupId, task.UserId, cancellationToken);
        }
    }

    private bool IsInCooldown(string key) =>
        _failureCooldowns.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow;

    private void RecordFailure(string key) =>
        _failureCooldowns[key] = DateTimeOffset.UtcNow + _options.FailureRetryAfter;

    private static string MemberCooldownKey(string groupId, string userId) => $"{groupId}:{userId}";

    private async Task RefreshGroupAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, CancellationToken cancellationToken)
    {
        var summary = await profileClient.GetGroupSummaryAsync(groupId, cancellationToken);
        if (summary is null)
        {
            logger.LogWarning("Group summary unavailable for group {GroupId}", groupId);
            RecordFailure(groupId);
            return;
        }

        _failureCooldowns.TryRemove(groupId, out _);
        await profileStore.UpsertGroupAsync(groupId, summary, cancellationToken);
    }

    private async Task RefreshMemberAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string userId, CancellationToken cancellationToken)
    {
        var profile = await profileClient.GetGroupMemberProfileAsync(groupId, userId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("Member profile unavailable for group {GroupId} user {UserId}", groupId, userId);
            RecordFailure(MemberCooldownKey(groupId, userId));
            return;
        }

        _failureCooldowns.TryRemove(MemberCooldownKey(groupId, userId), out _);
        await profileStore.UpsertMemberAsync(groupId, userId, profile, cancellationToken);
    }
}
