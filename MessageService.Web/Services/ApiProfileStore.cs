using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MessageService.Services;

/// <summary>Line 模式且 Line:OutboundHere=true 用：打 Db 端主機的 ingest API 查 TTL、
/// upsert 快取，取代 DbProfileStore 直接查資料庫的角色。頭貼快取是非關鍵資料
/// （見 IProfileStore 介面說明），這裡的例外一律往外拋，由 ProfileRefreshService
/// 既有的「記 log、不重試」處理，不需要額外的重試機制。</summary>
public class ApiProfileStore(IHttpClientFactory httpClientFactory, ILogger<ApiProfileStore> logger) : IProfileStore
{
    // 與 HttpIngestSink 的批次端點同一套處理：舊版 Core 沒有 profiles/stale 端點時回 404，
    // 只警告一次、當作沒有候選，避免升級過渡期每輪補刷都洗一筆 error log。
    // static 是因為 ApiProfileStore 是 scoped、每輪補刷各拿到新實例。
    private static int _staleEndpointNotFoundWarned;

    private HttpClient CreateClient() => httpClientFactory.CreateClient("ingest");

    public async Task<ProfileStaleness> GetStalenessAsync(
        string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // 手動組 query string（Uri.EscapeDataString 逐一編碼）而不是引入 System.Web.HttpUtility：
        // 只有三個固定參數，不值得為此多一個相依性
        var query = $"groupId={Uri.EscapeDataString(groupId)}&cutoff={Uri.EscapeDataString(cutoff.ToString("O"))}";
        if (userId is not null)
        {
            query += $"&userId={Uri.EscapeDataString(userId)}";
        }

        var result = await CreateClient().GetFromJsonAsync<ProfileStaleness>(
            $"api/ingest/profiles/staleness?{query}", cancellationToken);
        return result ?? new ProfileStaleness(true, userId is not null);
    }

    public async Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        using var response = await CreateClient().PostAsJsonAsync("api/ingest/profiles/group", summary, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        using var response = await CreateClient().PostAsJsonAsync(
            "api/ingest/profiles/member", new MemberUpsertRequest(groupId, profile), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ProfileRefreshTask>> GetStaleProfilesAsync(
        int max, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var query = $"max={max}&cutoff={Uri.EscapeDataString(cutoff.ToString("O"))}";
        using var response = await CreateClient().GetAsync($"api/ingest/profiles/stale?{query}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (Interlocked.CompareExchange(ref _staleEndpointNotFoundWarned, 1, 0) == 0)
            {
                logger.LogWarning(
                    "Core 端 ingest API 找不到 GET /api/ingest/profiles/stale（404）——可能還沒升級，" +
                    "背景補刷暫時沒有候選可掃。升級順序請先升 Core 再升 Edge，見 docs/DEPLOYMENT-MODES.md。");
            }
            return [];
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<ProfileRefreshTask>>(cancellationToken);
        return result ?? [];
    }
}
