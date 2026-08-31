using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        endpoints.MapGet("/edge-admin", (
            IConfiguration config,
            HttpContext context) =>
        {
            var saved = context.Request.Query.ContainsKey("saved");

            // 讀取目前生效設定值（包含加密設定檔覆蓋）
            var lineSecret = config["Line:ChannelSecret"];
            var lineToken = config["Line:ChannelAccessToken"];
            var ingestKey = config["Ingest:ApiKey"];
            var ingestIps = config.GetSection(PrefixIngestIps).Get<string[]>() ?? [];
            var webhookMode = config["WebhookSource:Mode"] ?? "Any";
            var webhookIps = config.GetSection(PrefixWebhookIps).Get<string[]>() ?? [];

            var model = new EdgeAdminViewModel(
                lineSecret,
                lineToken,
                ingestKey,
                ingestIps,
                webhookMode,
                webhookIps,
                Saved: saved);

            var html = EdgeAdminPage.Render(model);
            // 頁面帶有機密的末四碼，不能進瀏覽器磁碟快取或中間代理
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Content(html, "text/html; charset=utf-8");
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
            return Results.Redirect("/edge-admin?saved=true", permanent: false, preserveMethod: false);
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
}
