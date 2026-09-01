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
    string Via,
    /// <summary>這一列的判準是不是「2xx 才算成功」（名稱查詢那列）。false 代表判準是
    /// 「連得到就好」，顯示文字要用「可達／不可達」而不是「成功／失敗」。</summary>
    bool StrictSuccess = false);

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
            directHost: "api.line.me",
            clientName: LineProfileClient.HttpClientName,
            defaultBaseAddress: new Uri("https://api.line.me/"),
            requestUri: "v2/bot/info",
            overrideToken: overrideToken,
            strictSuccess: true,
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

                    // 契約是「成功要顯示 bot 名稱」；LINE 回應缺名稱時至少別讓說明欄空白
                    return (true, string.IsNullOrEmpty(displayName) ? "連線成功" : displayName);
                }

                var ex = new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
                return (false, OutboundFailureClassifier.Classify(ex, null));
            },
            via: via,
            cancellationToken: cancellationToken);
        results.Add(profileResult);

        // 2. 媒體內容 (api-data.line.me)
        var contentResult = await TestTargetAsync(
            purpose: "媒體內容",
            directHost: "api-data.line.me",
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
            directHost: "stickershop.line-scdn.net",
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
            directHost: "profile.line-scdn.net",
            clientName: LineProfileClient.ImageHttpClientName,
            defaultBaseAddress: null,
            requestUri: imageTargetUrl,
            overrideToken: null,
            evaluateResponse: ReachableOnAnyResponse,
            via: via,
            cancellationToken: cancellationToken,
            requiresProxyRewrite: true);
        results.Add(imageResult);

        return results;
    }

    /// <summary>公開 CDN 與 content API 這三個目標只驗「連得到」：收到任何 HTTP 回應就代表
    /// TCP/TLS 通了，404／401 都算通。**但 403 與 502／503／504 例外**——那是「鏈路被擋住」
    /// 的回應（proxy 的白名單擋掉、或 proxy 連不到 LINE），報成可達就等於把斷掉的鏈報成通的。</summary>
    private static Task<(bool Success, string Description)> ReachableOnAnyResponse(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = (int)response.StatusCode;
        var status = $"HTTP {code} {response.ReasonPhrase}".Trim();

        if (code is 403 or 502 or 503 or 504)
        {
            var ex = new HttpRequestException(status, null, response.StatusCode);
            return Task.FromResult((false, $"{status}——{OutboundFailureClassifier.Classify(ex, null)}"));
        }

        return Task.FromResult((true, $"可達（{status}）"));
    }

    private async Task<LineConnectivityTestResult> TestTargetAsync(
        string purpose,
        string directHost,
        string clientName,
        Uri? defaultBaseAddress,
        string requestUri,
        string? overrideToken,
        Func<HttpResponseMessage, CancellationToken, Task<(bool Success, string Description)>> evaluateResponse,
        string via,
        CancellationToken cancellationToken,
        bool strictSuccess = false,
        bool requiresProxyRewrite = false)
    {
        var options = monitor.CurrentValue;

        // 走 EdgeProxy 時實際連的是 proxy，不是 LINE 的網域——顯示 LINE 的 host 會讓人
        // 去開錯誤的防火牆洞（runtime log 也是用同一個推導）
        var target = HttpBaseAddress.ResolveOutboundHost(options, directHost);

        try
        {
            var client = httpClientFactory.CreateClient(clientName);

            // EdgeProxy 拓撲下 BaseAddress 必須已被改寫成 proxy 位址；此時補直連的 fallback
            // 會讓「proxy 沒生效」變成一次成功的直連測試，把斷掉的鏈報成通的
            // 頭貼那列沒有 BaseAddress（打絕對 URL），改用「URL 有沒有被改寫成 proxy 路徑」判斷
            var missingProxyRoute = options.OutboundVia is LineOutboundVia.EdgeProxy
                && (requiresProxyRewrite
                    ? !requestUri.Contains("/line/image/", StringComparison.Ordinal)
                    : client.BaseAddress is null && defaultBaseAddress is not null);

            if (missingProxyRoute)
            {
                return new LineConnectivityTestResult(
                    Purpose: purpose,
                    Target: target,
                    Success: false,
                    Description: "設定為經由 EdgeProxy，但這條路徑沒有 proxy 位址（多半是 Line:OutboundProxyBaseUrl 為空）",
                    Via: via,
                    StrictSuccess: strictSuccess);
            }

            if (client.BaseAddress is null && defaultBaseAddress is not null)
            {
                // 直連拓撲：具名 client 本來就不設 BaseAddress，由呼叫端補
                client.BaseAddress = defaultBaseAddress;
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
                Via: via,
                StrictSuccess: strictSuccess);
        }
        catch (Exception ex)
        {
            var errorMsg = OutboundFailureClassifier.Classify(ex, target);
            return new LineConnectivityTestResult(
                Purpose: purpose,
                Target: target,
                Success: false,
                Description: errorMsg,
                Via: via,
                StrictSuccess: strictSuccess);
        }
    }
}
