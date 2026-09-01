using System.Net.Http.Headers;
using System.Text.Json;
using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Services;

/// <summary>
/// LINE 單一目標連通性測試結果。
/// </summary>
public record LineConnectivityTestResult(
    string Purpose,
    string Target,
    bool Success,
    string Description,
    string Via);

/// <summary>
/// 提供 Edge 設定頁測試 LINE 四個網域 outbound 連通性與憑證有效性。
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

    public async Task<IReadOnlyList<LineConnectivityTestResult>> TestConnectivityAsync(
        string? overrideToken = null,
        CancellationToken cancellationToken = default)
    {
        var via = DetermineVia();
        var options = monitor.CurrentValue;
        var results = new List<LineConnectivityTestResult>(4);

        // 1. 名稱查詢 (api.line.me)
        var profileResult = await TestTargetAsync(
            purpose: "名稱查詢",
            target: "api.line.me",
            clientName: LineProfileClient.HttpClientName,
            defaultBaseAddress: new Uri("https://api.line.me/"),
            requestUri: "v2/bot/info",
            overrideToken: overrideToken,
            evaluateResponse: async (response, ct) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    string? displayName = null;
                    try
                    {
                        using var document = JsonDocument.Parse(body);
                        var root = document.RootElement;
                        if (root.TryGetProperty("displayName", out var dnElem) && dnElem.ValueKind == JsonValueKind.String)
                        {
                            displayName = dnElem.GetString();
                        }
                        if (string.IsNullOrEmpty(displayName) && root.TryGetProperty("basicId", out var biElem) && biElem.ValueKind == JsonValueKind.String)
                        {
                            displayName = biElem.GetString();
                        }
                    }
                    catch
                    {
                        // 忽略 JSON 解析例外
                    }

                    return (true, displayName ?? "");
                }

                var ex = new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
                return (false, OutboundFailureClassifier.Classify(ex, "api.line.me"));
            },
            via: via,
            cancellationToken: cancellationToken);
        results.Add(profileResult);

        // 2. 媒體內容 (api-data.line.me)
        var contentResult = await TestTargetAsync(
            purpose: "媒體內容",
            target: "api-data.line.me",
            clientName: LineContentClient.HttpClientName,
            defaultBaseAddress: new Uri("https://api-data.line.me/"),
            requestUri: "probe",
            overrideToken: null,
            evaluateResponse: ReachableOnAnyResponse,
            via: via,
            cancellationToken: cancellationToken);
        results.Add(contentResult);

        // 3. 貼圖 (stickershop.line-scdn.net)
        var stickerResult = await TestTargetAsync(
            purpose: "貼圖",
            target: "stickershop.line-scdn.net",
            clientName: LineContentClient.StickerHttpClientName,
            defaultBaseAddress: new Uri("https://stickershop.line-scdn.net/"),
            requestUri: "stickershop/v1/sticker/52002734/android/sticker.png",
            overrideToken: null,
            evaluateResponse: ReachableOnAnyResponse,
            via: via,
            cancellationToken: cancellationToken);
        results.Add(stickerResult);

        // 4. 頭貼 CDN (*.line-scdn.net)
        var proxyBaseAddress = options.OutboundVia == LineOutboundVia.EdgeProxy && !string.IsNullOrWhiteSpace(options.OutboundProxyBaseUrl)
            ? HttpBaseAddress.Create(options.OutboundProxyBaseUrl)
            : null;
        var imageTargetUrl = LineImageUrlRewriter.Rewrite("https://profile.line-scdn.net/probe", options.OutboundVia, proxyBaseAddress);

        var imageResult = await TestTargetAsync(
            purpose: "頭貼 CDN",
            target: "profile.line-scdn.net",
            clientName: LineProfileClient.ImageHttpClientName,
            defaultBaseAddress: null,
            requestUri: imageTargetUrl,
            overrideToken: null,
            evaluateResponse: ReachableOnAnyResponse,
            via: via,
            cancellationToken: cancellationToken);
        results.Add(imageResult);

        return results;
    }

    /// <summary>公開 CDN 與 content API 這三個目標只驗「連得到」：收到任何 HTTP 回應就代表
    /// TCP/TLS 通了（404／401 都算通），只有連不上才是防火牆問題。</summary>
    private static Task<(bool Success, string Description)> ReachableOnAnyResponse(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        Task.FromResult((true, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()));

    private async Task<LineConnectivityTestResult> TestTargetAsync(
        string purpose,
        string target,
        string clientName,
        Uri? defaultBaseAddress,
        string requestUri,
        string? overrideToken,
        Func<HttpResponseMessage, CancellationToken, Task<(bool Success, string Description)>> evaluateResponse,
        string via,
        CancellationToken cancellationToken)
    {
        var options = monitor.CurrentValue;

        try
        {
            var client = httpClientFactory.CreateClient(clientName);

            // EdgeProxy 拓撲下 BaseAddress 必須已被改寫成 proxy 位址；此時補直連的 fallback
            // 會讓「proxy 沒生效」變成一次成功的直連測試，把斷掉的鏈報成通的
            if (client.BaseAddress is null)
            {
                if (options.OutboundVia is LineOutboundVia.EdgeProxy && defaultBaseAddress is not null)
                {
                    return new LineConnectivityTestResult(
                        Purpose: purpose,
                        Target: target,
                        Success: false,
                        Description: "設定為經由 EdgeProxy，但 LINE 具名 client 沒有 proxy 位址（多半是 Line:OutboundProxyBaseUrl 為空）",
                        Via: via);
                }

                if (defaultBaseAddress is not null)
                {
                    // 直連拓撲：具名 client 本來就不設 BaseAddress，由呼叫端補
                    client.BaseAddress = defaultBaseAddress;
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(overrideToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overrideToken);
            }

            using var response = await client.SendAsync(request, cts.Token);
            var (success, description) = await evaluateResponse(response, cts.Token);

            return new LineConnectivityTestResult(
                Purpose: purpose,
                Target: target,
                Success: success,
                Description: description,
                Via: via);
        }
        catch (Exception ex)
        {
            var errorMsg = OutboundFailureClassifier.Classify(ex, target);
            return new LineConnectivityTestResult(
                Purpose: purpose,
                Target: target,
                Success: false,
                Description: errorMsg,
                Via: via);
        }
    }
}
