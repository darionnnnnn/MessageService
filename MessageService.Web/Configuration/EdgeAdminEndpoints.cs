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
            return Results.Content(html, "text/html; charset=utf-8");
        });

        endpoints.MapPost("/edge-admin", async (
            HttpContext context,
            EdgeSettingsStore store) =>
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

            // 2. 陣列欄位：先清掉該前綴所有的舊索引鍵，再寫入新項目
            RemoveArrayPrefix(updated, PrefixIngestIps);
            var ingestIpList = ParseLines(ingestIpsInput);
            for (var i = 0; i < ingestIpList.Count; i++)
            {
                updated[$"{PrefixIngestIps}:{i}"] = ingestIpList[i];
            }

            // 3. Webhook 來源限制模式
            var normalizedMode = string.Equals(webhookModeInput, "AllowlistOnly", StringComparison.OrdinalIgnoreCase)
                ? "AllowlistOnly"
                : "Any";
            updated["WebhookSource:Mode"] = normalizedMode;

            // 4. Webhook 允許來源 IP 陣列
            RemoveArrayPrefix(updated, PrefixWebhookIps);
            var webhookIpList = ParseLines(webhookIpsInput);
            for (var i = 0; i < webhookIpList.Count; i++)
            {
                updated[$"{PrefixWebhookIps}:{i}"] = webhookIpList[i];
            }

            // 儲存至加密設定檔並立即觸發 reload
            store.Save(updated);

            // PRG (Post-Redirect-Get)
            return Results.Redirect("/edge-admin?saved=true", permanent: false, preserveMethod: false);
        });
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
