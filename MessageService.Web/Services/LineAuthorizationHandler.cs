using System.Net.Http.Headers;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Services;

/// <summary>
/// 動態在出站 LINE HTTP 請求附加 Authorization: Bearer {token} 標頭，確保 token 熱更新時出站請求立即帶上最新值。
/// </summary>
public class LineAuthorizationHandler(IOptionsMonitor<LineOptions> monitor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", monitor.CurrentValue.ChannelAccessToken);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
