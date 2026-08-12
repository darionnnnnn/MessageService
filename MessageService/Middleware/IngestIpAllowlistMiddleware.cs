using System.Net;

namespace MessageService.Middleware;

/// <summary>限制誰能打 /api/ingest/*（Program.cs 用 UseWhen 只把這個中介層掛在該路徑群組，
/// 不影響 LINE webhook 端點）。跟 MessageService.Web/Middleware/IpAllowlistMiddleware.cs 是
/// 同一套邏輯的獨立複本——兩個專案互不參照，也沒有共用的基礎建設專案可放，這段～60 行邏輯
/// 穩定不常變動，用複製一份換取不為此新拉一個共用專案。空白名單視為全拒，寧嚴勿鬆。</summary>
public class IngestIpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IngestIpAllowlistMiddleware> _logger;
    private readonly List<IPNetwork> _allowedNetworks;

    public IngestIpAllowlistMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<IngestIpAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowedNetworks = ParseAllowedIps(configuration.GetSection("AllowedClientIps").Get<string[]>() ?? []);

        if (_allowedNetworks.Count == 0)
        {
            _logger.LogWarning("AllowedClientIps is empty — all ingest API requests will be rejected until it is configured");
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

        _logger.LogWarning("Rejected ingest API request from {RemoteIp} to {Path} (not in AllowedClientIps)", remoteIp, context.Request.Path);
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
