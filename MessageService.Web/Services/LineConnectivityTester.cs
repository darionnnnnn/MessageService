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
            client.BaseAddress ??= new Uri("https://api.line.me/");

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
