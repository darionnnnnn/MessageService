using System.Collections.Concurrent;
using MessageService.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>資料存取全部透過 IProfileStore（Full／Db 模式查本機 DB，Line 模式打 ingest API）——
/// 這裡只保留「查 TTL → 打 LINE → 過期才 upsert」的流程本身，不直接碰任何資料庫或 HTTP client。</summary>
public class ProfileRefreshService(
    IProfileRefreshQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ProfileCacheOptions> options,
    IOptions<IngestOptions> ingestOptions,
    TimeProvider timeProvider,
    ILogger<ProfileRefreshService> logger) : BackgroundService
{
    private readonly ProfileCacheOptions _options = options.Value;

    /// <summary>staleness 查詢打的是**內部通道**（Edge 打 Core 的 ingest API、其餘模式讀本機
    /// 資料庫），跟後面打 LINE API 是完全不同的方向。這兩個欄位讓失敗訊息標對目標——
    /// 標成 LINE 的網域會讓人去開錯誤的防火牆洞（實測踩過）。</summary>
    private readonly string _internalTarget = string.IsNullOrWhiteSpace(ingestOptions.Value.BaseUrl)
        ? "本機資料庫"
        : new Uri(HttpBaseAddress.Create(ingestOptions.Value.BaseUrl), "api/ingest/profiles/staleness").ToString();

    private readonly string? _internalHost = string.IsNullOrWhiteSpace(ingestOptions.Value.BaseUrl)
        ? null
        : HttpBaseAddress.Create(ingestOptions.Value.BaseUrl).Host;

    /// <summary>清理過期抑制項目的門檻：當項目數超過此值時進行清理。</summary>
    private const int CleanupThreshold = 1000;

    /// <summary>成功或查無過期時的抑制窗口：固定為 5 分鐘。
    /// 為什麼是 5 分鐘而不是 ProfileCacheOptions.RefreshAfter？
    /// RefreshAfter 預設是 7 天。GetStalenessAsync 回報「不 stale」，只代表該筆資料的
    /// UpdatedAt 落在 7 天內——可能是 6 天前更新的，再過 1 天就該刷新了。如果因為
    /// 「這次查到不 stale」就記成「未來 7 天都不用查」，那筆資料會被延後將近一整個週期才刷新，
    /// 等於把 TTL 悄悄變成最長兩倍。
    /// 5 分鐘的抑制窗口則完全避開這個問題：真正要解決的痛點是「同一個人連發一串訊息，
    /// 每則都查一次」的秒級到分鐘級突發；用 5 分鐘就能吃掉絕大部分重複，
    /// 而對 7 天的刷新週期毫無影響（最多延後 5 分鐘）。</summary>
    private static readonly TimeSpan SuppressWindow = TimeSpan.FromMinutes(5);

    /// <summary>**內部通道**（staleness 查詢）失敗的冷卻。刻意遠短於 FailureRetryAfter：
    /// 這類失敗最常見的成因是「Edge→Core 尚未切換到拉取模式」的過渡期，而那個過渡期只有
    /// PullActivationSeconds（預設 180 秒）。用 10 分鐘的冷卻會讓切換完成後的第一批派工
    /// 全被抑制掉，名稱／頭貼要等十幾分鐘才出得來（實測到的症狀）。
    /// 打 LINE API 的失敗不走這條，仍用 FailureRetryAfter——那是對方限流／故障，值得等久一點。</summary>
    private static readonly TimeSpan InternalFailureRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>程序內抑制表：group 用 GroupId 當鍵、member 用 "GroupId:UserId"。
    /// 包含打 LINE API 失敗後的退避冷卻（FailureRetryAfter），以及查詢確認不 stale 或 upsert 成功後的抑制窗口（SuppressWindow）。
    /// 在抑制時間截止之前完全不需要重複查詢或處理。單例 BackgroundService 的欄位，整個服務生命週期共用一份，重啟就重置。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _suppressUntil = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var task in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 含 HttpClient 逾時的 TaskCanceledException——穿出這個迴圈會讓服務靜默結束
                // （BackgroundService 預設 StopHost），判斷依據看 stoppingToken 不看例外型別
                logger.LogError(ex, "Unexpected error refreshing profile cache for group {GroupId} user {UserId}",
                    task.GroupId, task.UserId);
            }
        }
    }

    public async Task ProcessAsync(ProfileRefreshTask task, CancellationToken cancellationToken)
    {
        var groupSuppressed = IsSuppressed(task.GroupId);
        var memberSuppressed = task.UserId is null || IsSuppressed(MemberKey(task.GroupId, task.UserId));

        // 若 group 與 member 皆在抑制窗口內，直接短路返回，完全不建 scope 也省掉 staleness 查詢（尤其是 Edge 模式下的 HTTP round-trip）
        if (groupSuppressed && memberSuppressed)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var profileStore = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        var profileClient = scope.ServiceProvider.GetRequiredService<ILineProfileClient>();
        var targetHost = HttpBaseAddress.ResolveOutboundHost(
            scope.ServiceProvider.GetService<IOptionsMonitor<LineOptions>>()?.CurrentValue, "api.line.me");
        var cutoff = timeProvider.GetUtcNow() - _options.RefreshAfter;

        // 一次查完群組與成員的 staleness——TTL 判斷一定要在打 LINE API 之前完成才省得到配額，
        // 這也是 IProfileStore 把 staleness 跟 upsert 拆成兩支方法的理由
        ProfileStaleness staleness;
        try
        {
            staleness = await profileStore.GetStalenessAsync(task.GroupId, task.UserId, cutoff, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordInternalFailure(task.GroupId);
            if (task.UserId is not null)
            {
                RecordInternalFailure(MemberKey(task.GroupId, task.UserId));
            }

            logger.LogWarning(ex,
                "查詢名稱／頭貼快取狀態失敗 (group {GroupId}, user {UserId})，目標 {Target}; backing off: {FailureReason}",
                task.GroupId, task.UserId, _internalTarget,
                OutboundFailureClassifier.Classify(ex, _internalHost));
            return;
        }

        if (!groupSuppressed)
        {
            if (staleness.GroupStale)
            {
                await RefreshGroupAsync(profileStore, profileClient, task.GroupId, staleness.GroupPictureFetchedUrl, staleness.HasGroupPicture, targetHost, cancellationToken);
            }
            else
            {
                Suppress(task.GroupId, timeProvider.GetUtcNow() + SuppressWindow);
            }
        }

        if (task.UserId is not null && !memberSuppressed)
        {
            var memberKey = MemberKey(task.GroupId, task.UserId);
            if (staleness.MemberStale)
            {
                await RefreshMemberAsync(profileStore, profileClient, task.GroupId, task.UserId, staleness.MemberPictureFetchedUrl, staleness.HasMemberPicture, targetHost, cancellationToken);
            }
            else
            {
                Suppress(memberKey, timeProvider.GetUtcNow() + SuppressWindow);
            }
        }
    }

    private bool IsSuppressed(string key) =>
        _suppressUntil.TryGetValue(key, out var until) && until > timeProvider.GetUtcNow();

    private void Suppress(string key, DateTimeOffset until)
    {
        _suppressUntil[key] = until;
        if (_suppressUntil.Count > CleanupThreshold)
        {
            CleanupExpiredEntries();
        }
    }

    private void RecordFailure(string key) =>
        Suppress(key, timeProvider.GetUtcNow() + _options.FailureRetryAfter);

    /// <summary>內部通道失敗的短冷卻，見 InternalFailureRetryAfter。</summary>
    private void RecordInternalFailure(string key) =>
        Suppress(key, timeProvider.GetUtcNow() + InternalFailureRetryAfter);

    private void CleanupExpiredEntries()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _suppressUntil)
        {
            if (pair.Value <= now)
            {
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)_suppressUntil).Remove(pair);
            }
        }
    }

    private static string MemberKey(string groupId, string userId) => $"{groupId}:{userId}";

    // 例外也要記冷卻，不能只記「回 null」那條路。LineProfileClient 只有 HTTP 404 會回 null，
    // 其餘（429 rate limit、5xx、連線逾時）一律拋例外——而那些正是負向快取想擋的「暫時性故障」。
    // 例外若直接穿過 RecordFailure，冷卻條目根本不會寫進去，下一則訊息進來又立刻重打一次
    // LINE API：429 的情況等於用訊息速率持續加壓，把限流拖得更久。原本實際被保護到的只剩
    // 「bot 被踢出群組／使用者退群」這個 404 情境，跟這個功能的初衷正好相反。
    private async Task RefreshGroupAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string? knownPictureUrl, bool hasPicture, string? targetHost, CancellationToken cancellationToken)
    {
        GroupSummary? summary;
        try
        {
            summary = await profileClient.GetGroupSummaryAsync(groupId, knownPictureUrl, hasPicture, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(groupId);
            logger.LogWarning(ex, "Group summary lookup failed for group {GroupId}; backing off: {FailureReason}",
                groupId, OutboundFailureClassifier.Classify(ex, targetHost));
            return;
        }

        if (summary is null)
        {
            logger.LogWarning("Group summary unavailable for group {GroupId}", groupId);
            RecordFailure(groupId);
            return;
        }

        try
        {
            await profileStore.UpsertGroupAsync(groupId, summary, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(groupId);
            logger.LogWarning(ex, "Upserting group profile failed for group {GroupId}; backing off: {FailureReason}",
                groupId, OutboundFailureClassifier.Classify(ex, _internalHost));
            return;
        }

        // 永久不可得的圖不進冷卻：staleness 已經把它記成「試過了」，不會再被判為缺圖
        if (summary.PictureDownloadFailed)
        {
            RecordFailure(groupId);
        }
        else
        {
            Suppress(groupId, timeProvider.GetUtcNow() + SuppressWindow);
        }
    }

    private async Task RefreshMemberAsync(
        IProfileStore profileStore, ILineProfileClient profileClient, string groupId, string userId, string? knownPictureUrl, bool hasPicture, string? targetHost, CancellationToken cancellationToken)
    {
        MemberProfile? profile;
        try
        {
            profile = await profileClient.GetGroupMemberProfileAsync(groupId, userId, knownPictureUrl, hasPicture, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(MemberKey(groupId, userId));
            logger.LogWarning(ex, "Member profile lookup failed for group {GroupId} user {UserId}; backing off: {FailureReason}",
                groupId, userId, OutboundFailureClassifier.Classify(ex, targetHost));
            return;
        }

        if (profile is null)
        {
            logger.LogWarning("Member profile unavailable for group {GroupId} user {UserId}", groupId, userId);
            RecordFailure(MemberKey(groupId, userId));
            return;
        }

        try
        {
            await profileStore.UpsertMemberAsync(groupId, userId, profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(MemberKey(groupId, userId));
            logger.LogWarning(ex, "Upserting member profile failed for group {GroupId} user {UserId}; backing off: {FailureReason}",
                groupId, userId, OutboundFailureClassifier.Classify(ex, _internalHost));
            return;
        }

        if (profile.PictureDownloadFailed)
        {
            RecordFailure(MemberKey(groupId, userId));
        }
        else
        {
            Suppress(MemberKey(groupId, userId), timeProvider.GetUtcNow() + SuppressWindow);
        }
    }
}
