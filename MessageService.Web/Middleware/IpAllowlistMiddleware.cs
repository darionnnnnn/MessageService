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
    private readonly ILogger<IpAllowlistMiddleware> _logger;
    private readonly IpAllowlistOptions _options;
    private readonly List<IPNetwork> _allowedNetworks;

    public IpAllowlistMiddleware(
        RequestDelegate next, IConfiguration configuration, ILogger<IpAllowlistMiddleware> logger, IpAllowlistOptions options)
    {
        _next = next;
        _logger = logger;
        _options = options;
        _allowedNetworks = ParseAllowedIps(configuration.GetSection(options.ConfigSectionName).Get<string[]>() ?? []);

        if (_allowedNetworks.Count == 0)
        {
            _logger.LogWarning(
                "{Label}: {Section} is empty — all requests will be rejected until it is configured",
                _options.Label, _options.ConfigSectionName);
        }
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
        var normalizedIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        return _allowedNetworks.Any(network => network.Contains(normalizedIp));
    }

    private static List<IPNetwork> ParseAllowedIps(IReadOnlyList<string> entries)
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (entry.Contains('/') && IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
        }

        return networks;
    }
}
