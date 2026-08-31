using System.Net;

namespace MessageService.Web.Middleware;

/// <summary>設定要讀哪個 config section、log 訊息要用哪個標籤——同一顆中介層類別同時服務
/// 檢視端白名單（Viewer:AllowedClientIps）與 ingest API 白名單（Ingest:AllowedClientIps）。
/// 合併前這是兩個專案（各自一份 appsettings.json）裡幾乎一模一樣的複本；合併成單一專案、
/// 單一 appsettings.json 之後，如果兩處還讀同一個 key，viewer 與 ingest 的白名單會被迫共用
/// 同一份清單——這在真實拆機拓撲下是錯的（office LAN 不該同時也是 ingest 的允許來源），
/// 所以趁合併順手把 key 拆開，不留到 Stage 2 才處理。</summary>
public record IpAllowlistOptions(string ConfigSectionName, string Label);

/// <summary>沒有登入機制時的最低防護。空白名單視為全拒，寧嚴勿鬆。
/// 支援單一 IP（"127.0.0.1"）與 CIDR 網段（"10.1.0.0/24"）。</summary>
public class IpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IpAllowlistMiddleware> _logger;
    private readonly IpAllowlistOptions _options;

    private readonly object _lock = new();
    private string[] _cachedRawEntries = [];
    private List<IPNetwork> _cachedNetworks = [];
    private bool _hasWarnedEmpty;

    public IpAllowlistMiddleware(
        RequestDelegate next, IConfiguration configuration, ILogger<IpAllowlistMiddleware> logger, IpAllowlistOptions options)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
        _options = options;

        // 第一次啟動時解析並在為空時記警告
        GetAllowedNetworks();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null && IsAllowed(remoteIp))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "{Label}: rejected request from {RemoteIp} to {Path} (not in {Section})",
            _options.Label, remoteIp, context.Request.Path, _options.ConfigSectionName);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
    }

    private bool IsAllowed(IPAddress remoteIp)
    {
        var networks = GetAllowedNetworks();
        return IpNetworkParser.IsAllowed(remoteIp, networks);
    }

    private List<IPNetwork> GetAllowedNetworks()
    {
        var rawEntries = _configuration.GetSection(_options.ConfigSectionName).Get<string[]>() ?? [];
        lock (_lock)
        {
            if (_cachedRawEntries.SequenceEqual(rawEntries, StringComparer.Ordinal))
            {
                return _cachedNetworks;
            }

            _cachedNetworks = IpNetworkParser.ParseAllowedIps(rawEntries, _logger, _options.Label, _options.ConfigSectionName);
            _cachedRawEntries = rawEntries;

            if (_cachedNetworks.Count == 0 && !_hasWarnedEmpty)
            {
                _hasWarnedEmpty = true;
                _logger.LogWarning(
                    "{Label}: {Section} is empty — all requests will be rejected until it is configured",
                    _options.Label, _options.ConfigSectionName);
            }

            return _cachedNetworks;
        }
    }
}

/// <summary>
/// IP 與 CIDR 網段解析與匹配的共用小工具。
/// </summary>
internal static class IpNetworkParser
{
    public static bool IsAllowed(IPAddress remoteIp, IReadOnlyList<IPNetwork> networks)
    {
        var normalizedIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        return networks.Any(network => network.Contains(normalizedIp));
    }

    public static List<IPNetwork> ParseAllowedIps(
        IReadOnlyList<string> entries,
        ILogger? logger = null,
        string? label = null,
        string? sectionName = null)
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out var network))
                {
                    logger?.LogWarning(
                        "{Label}: {Section} 有一條 CIDR 網段解析失敗並略過：\"{Entry}\"。" +
                        "IPNetwork 要求主機位元全為 0，例如 \"10.1.0.5/24\" 請改成 \"10.1.0.0/24\"" +
                        "（若只要允許單一位址則改成 \"10.1.0.5/32\"）。",
                        label ?? "IP", sectionName ?? "Allowlist", entry);
                    continue;
                }
                networks.Add(network);
                continue;
            }

            if (!IPAddress.TryParse(entry, out var address))
            {
                logger?.LogWarning(
                    "{Label}: {Section} 有一條設定值不是合法的 IP 或 CIDR 網段並略過：\"{Entry}\"。",
                    label ?? "IP", sectionName ?? "Allowlist", entry);
                continue;
            }
            networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
        }

        return networks;
    }
}

