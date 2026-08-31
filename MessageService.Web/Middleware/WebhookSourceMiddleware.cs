using System.Net;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Middleware;

/// <summary>
/// Webhook 來源 IP 檢查中介層。
/// 當 WebhookSource:Mode == AllowlistOnly 時，只放行 WebhookSource:AllowedIps 清單內的來源 IP，其餘回傳 403 Forbidden；
/// 當 WebhookSource:Mode == Any 時完全不檢查（零行為差異）。
/// 支援透過 IOptionsMonitor 動態熱生效。
/// </summary>
public class WebhookSourceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<WebhookSourceOptions> _optionsMonitor;
    private readonly ILogger<WebhookSourceMiddleware> _logger;

    private readonly object _lock = new();
    private string[] _cachedRawEntries = [];
    private List<IPNetwork> _cachedNetworks = [];
    private bool _hasWarnedEmpty;

    public WebhookSourceMiddleware(
        RequestDelegate next,
        IOptionsMonitor<WebhookSourceOptions> optionsMonitor,
        ILogger<WebhookSourceMiddleware> logger)
    {
        _next = next;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _optionsMonitor.CurrentValue;

        // Mode == Any 時零行為差異，完全不檢查
        if (options.Mode is not WebhookSourceMode.AllowlistOnly)
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null && IsAllowed(remoteIp, options.AllowedIps))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "webhook-source: rejected request from {RemoteIp} to {Path} (not in WebhookSource:AllowedIps)",
            remoteIp, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
    }

    private bool IsAllowed(IPAddress remoteIp, string[] rawEntries)
    {
        var networks = GetAllowedNetworks(rawEntries);
        return IpNetworkParser.IsAllowed(remoteIp, networks);
    }

    private List<IPNetwork> GetAllowedNetworks(string[] rawEntries)
    {
        rawEntries ??= [];
        lock (_lock)
        {
            if (_cachedRawEntries.SequenceEqual(rawEntries, StringComparer.Ordinal))
            {
                return _cachedNetworks;
            }

            _cachedNetworks = IpNetworkParser.ParseAllowedIps(
                rawEntries, _logger, "webhook-source", WebhookSourceOptions.SectionName);
            _cachedRawEntries = rawEntries;

            if (_cachedNetworks.Count == 0 && !_hasWarnedEmpty)
            {
                _hasWarnedEmpty = true;
                _logger.LogWarning(
                    "webhook-source: WebhookSource:AllowedIps is empty — all requests will be rejected until it is configured");
            }
            else if (_cachedNetworks.Count > 0)
            {
                // 如果之後更新了白名單，重設 warning 標記
                _hasWarnedEmpty = false;
            }

            return _cachedNetworks;
        }
    }
}
