using System.Net.Http.Headers;
using MessageService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Services;

/// <summary>
/// 動態在出站 LINE HTTP 請求附加 Authorization: Bearer {token} 標頭，確保 token 熱更新時出站請求立即帶上最新值。
/// </summary>
public class LineAuthorizationHandler(
    IOptionsMonitor<LineOptions> monitor,
    ILogger<LineAuthorizationHandler> logger) : DelegatingHandler
{
    private readonly object _syncLock = new();
    private DateTimeOffset? _lastWarningAt;
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(10);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var token = monitor.CurrentValue.ChannelAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                LogEmptyTokenWarning();
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    private void LogEmptyTokenWarning()
    {
        lock (_syncLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastWarningAt is not { } last || now - last >= LogInterval)
            {
                _lastWarningAt = now;
                logger.LogWarning("Line:ChannelAccessToken 為空，LINE API 呼叫將失敗，請在 /edge-admin 設定頁重新填寫。");
            }
        }
    }
}

