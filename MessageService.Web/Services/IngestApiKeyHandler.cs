using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Services;

/// <summary>
/// 動態在出站 HTTP 請求附加 X-Ingest-Key 標頭，確保金鑰熱更新時出站請求立即帶上最新值。
/// </summary>
public class IngestApiKeyHandler(IOptionsMonitor<IngestOptions> monitor) : DelegatingHandler
{
    public const string HeaderName = "X-Ingest-Key";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(HeaderName); // 防重複
        request.Headers.TryAddWithoutValidation(HeaderName, monitor.CurrentValue.ApiKey ?? "");
        return base.SendAsync(request, cancellationToken);
    }
}
