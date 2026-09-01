using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MessageService.Web.Services;

/// <summary>DNS 查詢的縫隙，讓測試不必真的打 DNS。</summary>
public interface IDnsLookup
{
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken);
}

/// <summary>正式環境的 DNS 查詢。</summary>
public sealed class SystemDnsLookup : IDnsLookup
{
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>把 outbound 目標的 host 解析成 IP，供診斷訊息標示「這次實際要連的是誰」。
///
/// 網管核對防火牆時要的是 IP，不是網域名稱——CDN 與 LINE API 的 A 記錄會變，
/// 只寫網域的錯誤訊息沒辦法直接對照防火牆規則。
///
/// 解析是 **best-effort**：失敗、逾時都不拋例外，回 null 讓呼叫端照常回報原本的錯誤。
/// 解析結果（含失敗）快取 60 秒——重試風暴下每次失敗都查一次 DNS 只會讓情況更糟，
/// 而失敗結果不快取的話每次都要等滿逾時。</summary>
public class OutboundTargetResolver(TimeProvider timeProvider, IDnsLookup dnsLookup)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, (string? Ip, DateTimeOffset ExpiresAt)> cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>回傳逗號分隔的 IP 清單（IPv4 排前面，防火牆規則多半只認 IPv4），
    /// 解析不出來時回 null。</summary>
    public async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (cache.TryGetValue(host, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Ip;
        }

        string? resolved = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(LookupTimeout);

            var addresses = await dnsLookup.GetHostAddressesAsync(host, cts.Token);
            if (addresses.Length > 0)
            {
                resolved = string.Join(", ", addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .Select(a => a.ToString()));
            }
        }
        catch (Exception)
        {
            // best-effort：解析不到就讓呼叫端顯示「IP 解析失敗」，不影響原本要回報的錯誤
        }

        cache[host] = (resolved, now.Add(CacheTtl));
        return resolved;
    }

    /// <summary>診斷訊息裡「目標」欄位的統一寫法，各 log 點不要自己拼字串。</summary>
    public static string FormatTarget(string host, string? ip)
        => string.IsNullOrEmpty(ip) ? $"{host}（IP 解析失敗）" : $"{host}（IP：{ip}）";

    /// <summary>解析並直接組出顯示字串的便利方法。</summary>
    public async Task<string> ResolveAndFormatAsync(string host, CancellationToken cancellationToken = default)
        => FormatTarget(host, await ResolveAsync(host, cancellationToken));
}
