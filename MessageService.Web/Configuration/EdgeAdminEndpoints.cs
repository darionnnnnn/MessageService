using System.Net.Http.Json;
using System.Text;
using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Diagnostics;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Configuration;

/// <summary>
/// Edge 管理設定頁 minimal API 端點註冊。
/// 僅在 Edge 模式下由 MessageServiceRequestPipelineExtensions 呼叫註冊。
/// </summary>
public static class EdgeAdminEndpoints
{
    private const string PrefixIngestIps = "Ingest:AllowedClientIps";
    private const string PrefixWebhookIps = "WebhookSource:AllowedIps";

    public static void MapEdgeAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/edge-admin", async (
            IConfiguration config,
            EdgeSettingsStore store,
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            LogRingBuffer ringBuffer,
            DeploymentCapabilities capabilities) =>
        {
            var model = await BuildViewModelAsync(
                config, store, httpClientFactory, ringBuffer, capabilities,
                saved: context.Request.Query.ContainsKey("saved"));

            return RenderPage(context, model);
        });

        endpoints.MapPost("/edge-admin/test-line", async (
            HttpContext context,
            IConfiguration config,
            EdgeSettingsStore store,
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<LineOptions> lineOptionsMonitor,
            DeploymentCapabilities capabilities,
            LogRingBuffer ringBuffer,
            OutboundTargetResolver targetResolver,
            ILogger<LineConnectivityTester> testerLogger) =>
        {
            // OutboundHere=false 的主機根本沒有 LINE 具名 client，直接渲染頁面上的說明即可
            IReadOnlyList<LineConnectivityTestResult>? testResults = null;
            if (capabilities.OutboundHere)
            {
                var form = await context.Request.ReadFormAsync();
                var overrideToken = form["overrideToken"].ToString();
                var tokenToUse = string.IsNullOrWhiteSpace(overrideToken) ? null : overrideToken.Trim();

                var tester = new LineConnectivityTester(
                    httpClientFactory, lineOptionsMonitor, targetResolver, testerLogger);
                testResults = await tester.TestConnectivityAsync(tokenToUse, context.RequestAborted);
            }

            var model = await BuildViewModelAsync(
                config, store, httpClientFactory, ringBuffer, capabilities,
                saved: false, lineTestResults: testResults, activeTab: "connection");

            return RenderPage(context, model);
        });

        endpoints.MapPost("/edge-admin", async (
            HttpContext context,
            EdgeSettingsStore store,
            IConfiguration configuration) =>
        {
            var form = await context.Request.ReadFormAsync();

            var lineSecretInput = form["lineChannelSecret"].ToString();
            var lineTokenInput = form["lineChannelAccessToken"].ToString();
            var ingestKeyInput = form["ingestApiKey"].ToString();
            var ingestIpsInput = form["ingestAllowedClientIps"].ToString();
            var webhookModeInput = form["webhookSourceMode"].ToString();
            var webhookIpsInput = form["webhookSourceAllowedIps"].ToString();

            // 讀取目前加密檔現有設定（以其為基底更新，機密欄位留空時維持原值）
            var existing = store.Read();
            var updated = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);

            // 1. 機密欄位：留空＝維持原值（不覆蓋成空字串）
            if (!string.IsNullOrEmpty(lineSecretInput))
            {
                updated["Line:ChannelSecret"] = lineSecretInput;
            }
            if (!string.IsNullOrEmpty(lineTokenInput))
            {
                updated["Line:ChannelAccessToken"] = lineTokenInput;
            }
            if (!string.IsNullOrEmpty(ingestKeyInput))
            {
                updated["Ingest:ApiKey"] = ingestKeyInput;
            }

            // 2. 陣列欄位
            WriteArray(updated, PrefixIngestIps, ParseLines(ingestIpsInput), configuration);

            // 3. Webhook 來源限制模式
            var normalizedMode = string.Equals(webhookModeInput, "AllowlistOnly", StringComparison.OrdinalIgnoreCase)
                ? "AllowlistOnly"
                : "Any";
            updated["WebhookSource:Mode"] = normalizedMode;

            // 4. Webhook 允許來源 IP 陣列
            WriteArray(updated, PrefixWebhookIps, ParseLines(webhookIpsInput), configuration);

            // 儲存至加密設定檔並立即觸發 reload
            store.Save(updated);

            // PRG (Post-Redirect-Get)
            return Results.Redirect($"{context.Request.PathBase}/edge-admin?saved=true", permanent: false, preserveMethod: false);
        });
    }

    /// <summary>把陣列欄位寫進加密設定檔。
    ///
    /// **不能只寫新項目就算數**：加密來源是「疊在 appsettings 之上」的，設定系統逐鍵合併——
    /// appsettings 有 3 筆而使用者改成 2 筆時，只寫 `:0`、`:1` 的話 appsettings 的 `:2`
    /// 仍然存在、仍然生效，被移除的那一筆其實沒被移除（清空整份清單時更嚴重：加密檔
    /// 完全沒有該前綴的鍵，appsettings 的整份清單原封生效）。
    ///
    /// 所以多出來的索引要用**空字串哨兵**覆蓋掉——`IpNetworkParser.ParseAllowedIps` 會略過空白，
    /// 等同於該筆不存在。覆蓋範圍取「新清單長度」與「目前設定實際看得到的筆數」的較大值。</summary>
    private static void WriteArray(
        IDictionary<string, string?> dict, string prefix, IReadOnlyList<string> values, IConfiguration configuration)
    {
        RemoveArrayPrefix(dict, prefix);

        for (var i = 0; i < values.Count; i++)
        {
            dict[$"{prefix}:{i}"] = values[i];
        }

        // 合併後（含 appsettings）目前總共有幾筆——這才是要蓋掉的範圍
        var effectiveCount = configuration.GetSection(prefix).GetChildren().Count();
        for (var i = values.Count; i < effectiveCount; i++)
        {
            dict[$"{prefix}:{i}"] = "";
        }
    }

    private static void RemoveArrayPrefix(IDictionary<string, string?> dict, string prefix)
    {
        var keysToRemove = dict.Keys
            .Where(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            dict.Remove(key);
        }
    }

    private static List<string> ParseLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    /// <summary>頁面帶有機密的末四碼，不能進瀏覽器磁碟快取或中間代理。</summary>
    private static IResult RenderPage(HttpContext context, EdgeAdminViewModel model)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Content(EdgeAdminPage.Render(model, context.Request.PathBase.Value ?? ""), "text/html; charset=utf-8");
    }

    /// <summary>GET 與連線測試 POST 都要渲染同一張頁面，檢視模型的組法只寫這一份。</summary>
    private static async Task<EdgeAdminViewModel> BuildViewModelAsync(
        IConfiguration config,
        EdgeSettingsStore store,
        IHttpClientFactory httpClientFactory,
        LogRingBuffer ringBuffer,
        DeploymentCapabilities capabilities,
        bool saved,
        IReadOnlyList<LineConnectivityTestResult>? lineTestResults = null,
        string? activeTab = null)
    {
        // 錯誤排查：本機緩衝快照、今日 log 檔尾、EdgeProxy 端錯誤
        var localErrors = ringBuffer.Snapshot();
        var (todayLogContent, todayLogErrorMessage) = ReadTodayLogTail();
        var (proxyErrors, proxyStatusMessage) = await FetchProxyErrorsAsync(httpClientFactory, config);

        return new EdgeAdminViewModel(
            // 讀取目前生效設定值（包含加密設定檔覆蓋）
            config["Line:ChannelSecret"],
            config["Line:ChannelAccessToken"],
            config["Ingest:ApiKey"],
            config.GetSection(PrefixIngestIps).Get<string[]>() ?? [],
            config["WebhookSource:Mode"] ?? "Any",
            config.GetSection(PrefixWebhookIps).Get<string[]>() ?? [],
            Saved: saved,
            IsUnreadable: store.LoadStatus == EncryptedSettingsLoadStatus.Unreadable,
            LocalErrors: localErrors,
            TodayLogContent: todayLogContent,
            TodayLogErrorMessage: todayLogErrorMessage,
            ProxyErrors: proxyErrors,
            ProxyStatusMessage: proxyStatusMessage,
            OutboundHere: capabilities.OutboundHere,
            LineTestResults: lineTestResults,
            ActiveTab: activeTab);
    }

    /// <summary>log 檔名與位置的唯一真值來源，對應 nlog.config 的
    /// <c>${basedir}/logs/messageservice-${shortdate}.log</c>——改一邊要記得改另一邊。
    /// 用 AppContext.BaseDirectory 而不是 ContentRoot：NLog 的 ${basedir} 是組件目錄，
    /// 兩者在 IIS 發佈下相同，但 dotnet run 或自訂 --contentRoot 時會分岔，
    /// 分岔的症狀是頁面永遠顯示「今天尚無 log 檔」。</summary>
    private static string TodayLogPath() =>
        Path.Combine(AppContext.BaseDirectory, "logs", $"messageservice-{DateTime.Now:yyyy-MM-dd}.log");

    private static (string? Content, string? ErrorMessage) ReadTodayLogTail() => ReadLogTail(TodayLogPath());

    /// <summary>吃路徑的版本：測試要驗「檔案不存在」「超過 100 行只留尾巴」時不能用實際的
    /// log 目錄——NLog 在測試執行期間就會在那裡寫出當天的檔案。</summary>
    public static (string? Content, string? ErrorMessage) ReadLogTail(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return (null, "今天尚無 log 檔");
        }

        try
        {
            const int maxLines = 100;
            var queue = new Queue<string>(maxLines);
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (queue.Count >= maxLines)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(line);
            }

            return (string.Join("\n", queue), null);
        }
        catch (Exception ex)
        {
            return (null, $"無法讀取 log 檔：{ex.Message}");
        }
    }

    private static async Task<(IReadOnlyList<LogBufferEntry>? Entries, string? StatusMessage)> FetchProxyErrorsAsync(
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        var outboundVia = config["Line:OutboundVia"];
        var isEdgeProxy = string.Equals(outboundVia, "EdgeProxy", StringComparison.OrdinalIgnoreCase);
        var outboundProxyBaseUrl = config["Line:OutboundProxyBaseUrl"];

        if (!isEdgeProxy || string.IsNullOrWhiteSpace(outboundProxyBaseUrl))
        {
            return (null, "本主機未使用 EdgeProxy");
        }

        try
        {
            var client = httpClientFactory.CreateClient("edge-proxy-errors");
            var baseUri = HttpBaseAddress.Create(outboundProxyBaseUrl);
            var requestUri = new Uri(baseUri, "proxy-admin/errors");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await client.GetAsync(requestUri, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"無法連上 EdgeProxy：HTTP {(int)response.StatusCode} {response.ReasonPhrase}——請直接查看該主機的 logs 目錄");
            }

            var data = await response.Content.ReadFromJsonAsync<ProxyAdminErrorsResponse>(cancellationToken: cts.Token);
            if (data is null)
            {
                return (null, "無法連上 EdgeProxy：回應內容為空——請直接查看該主機的 logs 目錄");
            }

            // 主機名回填進區塊標題，讓管理者確認拉到的是哪一台 proxy（多台 proxy 或改過設定時會用到）
            return (data.Entries, $"來源主機：{data.MachineName}");
        }
        catch (Exception ex)
        {
            return (null, $"無法連上 EdgeProxy：{ex.Message}——請直接查看該主機的 logs 目錄");
        }
    }
}
