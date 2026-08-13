using System.Net.Http.Json;

namespace MessageService.Services;

/// <summary>Line 模式且 Line:OutboundHere=true 用：打 Db 端主機的 ingest API 查 TTL、
/// upsert 快取，取代 DbProfileStore 直接查資料庫的角色。頭貼快取是非關鍵資料
/// （見 IProfileStore 介面說明），這裡的例外一律往外拋，由 ProfileRefreshService
/// 既有的「記 log、不重試」處理，不需要額外的重試機制。</summary>
public class ApiProfileStore(IHttpClientFactory httpClientFactory) : IProfileStore
{
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
}
