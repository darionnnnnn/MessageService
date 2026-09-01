using System.Net.Http.Headers;
using System.Text.Json;
using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Services;

/// <summary>
/// LINE 連通性測試結果。
/// </summary>
public record LineConnectivityTestResult(
    bool Success,
    string? BotDisplayName,
    string? ErrorMessage,
    string Via);

/// <summary>
/// 提供 Edge 設定頁測試 LINE outbound 連通性與憑證有效性。
/// </summary>
public class LineConnectivityTester(IHttpClientFactory httpClientFactory, IOptionsMonitor<LineOptions> monitor)
{
    private string DetermineVia()
    {
        var options = monitor.CurrentValue;
        if (options.OutboundVia == LineOutboundVia.EdgeProxy && !string.IsNullOrWhiteSpace(options.OutboundProxyBaseUrl))
        {
            return $"EdgeProxy({options.OutboundProxyBaseUrl})";
        }
        if (options.OutboundVia == LineOutboundVia.EdgeProxy)
        {
            return "EdgeProxy";
        }
        return "Direct";
    }

    public async Task<LineConnectivityTestResult> TestConnectivityAsync(string? overrideToken = null, CancellationToken cancellationToken = default)
    {
        var via = DetermineVia();

        try
        {
            var client = httpClientFactory.CreateClient(LineProfileClient.HttpClientName);

            // EdgeProxy 拓撲下 BaseAddress 必須已被改寫成 proxy 位址；此時補直連的 fallback
            // 會讓「proxy 沒生效」變成一次成功的直連測試，把斷掉的鏈報成通的
            if (client.BaseAddress is null)
            {
                if (monitor.CurrentValue.OutboundVia is LineOutboundVia.EdgeProxy)
                {
                    return new LineConnectivityTestResult(
                        Success: false,
                        BotDisplayName: null,
                        ErrorMessage: "設定為經由 EdgeProxy，但 LINE 具名 client 沒有 proxy 位址"
                            + "（多半是 Line:OutboundProxyBaseUrl 空白，或改過設定後尚未重啟站台）",
                        Via: via);
                }

                // 直連拓撲：具名 client 本來就不設 BaseAddress，由呼叫端補（見 LineProfileClient）
                client.BaseAddress = new Uri("https://api.line.me/");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, "v2/bot/info");
            if (!string.IsNullOrWhiteSpace(overrideToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overrideToken);
            }

            using var response = await client.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                string? displayName = null;
                if (root.TryGetProperty("displayName", out var dnElem) && dnElem.ValueKind == JsonValueKind.String)
                {
                    displayName = dnElem.GetString();
                }
                if (string.IsNullOrEmpty(displayName) && root.TryGetProperty("basicId", out var biElem) && biElem.ValueKind == JsonValueKind.String)
                {
                    displayName = biElem.GetString();
                }
                displayName ??= "";

                return new LineConnectivityTestResult(
                    Success: true,
                    BotDisplayName: displayName,
                    ErrorMessage: null,
                    Via: via);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                var snippet = body.Length > 200 ? body[..200] : body;
                var statusCode = (int)response.StatusCode;
                var reason = string.IsNullOrWhiteSpace(snippet)
                    ? $"HTTP {statusCode} {response.ReasonPhrase}".Trim()
                    : $"HTTP {statusCode} {response.ReasonPhrase}: {snippet}".Trim();

                return new LineConnectivityTestResult(
                    Success: false,
                    BotDisplayName: null,
                    ErrorMessage: reason,
                    Via: via);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"{ex.GetType().Name}: {ex.Message}";
            return new LineConnectivityTestResult(
                Success: false,
                BotDisplayName: null,
                ErrorMessage: errorMsg,
                Via: via);
        }
    }
}
