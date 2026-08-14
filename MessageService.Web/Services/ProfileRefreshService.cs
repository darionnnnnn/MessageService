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
            await RefreshGroupAsync(profileStore, profileClient, task.GroupId, staleness.GroupPictureFetchedUrl, staleness.HasGroupPicture, cancellationToken);
        }

        if (task.UserId is not null && staleness.MemberStale && !IsInCooldown(MemberCooldownKey(task.GroupId, task.UserId)))
        {
            await RefreshMemberAsync(profileStore, profileClient, task.GroupId, task.UserId, staleness.MemberPictureFetchedUrl, staleness.HasMemberPicture, cancellationToken);
        }
    }

    private bool IsInCooldown(string key) =>
        _failureCooldowns.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow;

    private void RecordFailure(string key) =>
        _failureCooldowns[key] = DateTimeOffset.UtcNow + _options.FailureRetryAfter;

    private static string MemberCooldownKey(string groupId, string userId) => $"{groupId}:{userId}";

    // 例外也要記冷卻，不能只記「回 null」那條路。LineProfileClient 只有 HTTP 404 會回 null，
    // 其餘（429 rate limit、5xx、連線逾時）一律拋例外——而那些正是負向快取想擋的「暫時性故障」。
    // 例外若直接穿過 RecordFailure，冷卻條目根本不會寫進去，下一則訊息進來又立刻重打一次
    // LINE API：429 的情況等於用訊息速率持續加壓，把限流拖得更久。原本實際被保護到的只剩
    // 「bot 被踢出群組／使用者退群」這個 404 情境，跟這個功能的初衷正好相反。
    private async Task RefreshGroupAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        GroupSummary? summary;
        try
        {
            summary = await profileClient.GetGroupSummaryAsync(groupId, knownPictureUrl, hasPicture, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(groupId);
            logger.LogWarning(ex, "Group summary lookup failed for group {GroupId}; backing off", groupId);
            return;
        }

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
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string userId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        MemberProfile? profile;
        try
        {
            profile = await profileClient.GetGroupMemberProfileAsync(groupId, userId, knownPictureUrl, hasPicture, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(MemberCooldownKey(groupId, userId));
            logger.LogWarning(ex, "Member profile lookup failed for group {GroupId} user {UserId}; backing off", groupId, userId);
            return;
        }

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
