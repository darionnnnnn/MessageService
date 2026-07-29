using System.Net;

namespace MessageService.Web.Middleware;

/// <summary>
/// 全站 IP 白名單守門（沒有登入機制時的最低防護）。空白名單視為全拒，寧嚴勿鬆。
/// 支援單一 IP（"127.0.0.1"）與 CIDR 網段（"10.1.0.0/24"）。
/// </summary>
public class IpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IpAllowlistMiddleware> _logger;
    private readonly List<IPNetwork> _allowedNetworks;

    public IpAllowlistMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<IpAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowedNetworks = ParseAllowedIps(configuration.GetSection("AllowedClientIps").Get<string[]>() ?? []);

        if (_allowedNetworks.Count == 0)
        {
            _logger.LogWarning("AllowedClientIps is empty — all requests will be rejected until it is configured");
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

        _logger.LogWarning("Rejected request from {RemoteIp} to {Path} (not in AllowedClientIps)", remoteIp, context.Request.Path);
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
