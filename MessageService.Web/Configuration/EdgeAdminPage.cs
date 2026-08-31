using System.Net;

namespace MessageService.Web.Configuration;

/// <summary>
/// Edge 端設定頁的 View Model。
/// </summary>
public record EdgeAdminViewModel(
    string? LineChannelSecret,
    string? LineChannelAccessToken,
    string? IngestApiKey,
    IReadOnlyList<string>? IngestAllowedClientIps,
    string? WebhookSourceMode,
    IReadOnlyList<string>? WebhookSourceAllowedIps,
    bool Saved = false);

/// <summary>
/// 提供純函式產生 Edge 端管理設定頁 HTML。
/// 不依賴任何外部 CSS/JS/字型，整頁自包含，所有設定值皆做 HTML 逸出。
/// </summary>
public static class EdgeAdminPage
{
    public static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "";
        }

        // 末四碼只有在「露出來也還剩夠多沒露」時才顯示。長度剛好 4 會把整串印出來，
        // 5~7 也會露出一半以上——遮罩的意義是讓人辨認「是不是我設的那把」，
        // 不是把金鑰放到畫面上。門檻設在 8：至少要有一半以上仍被遮住
        const int minLengthToRevealTail = 8;
        if (secret.Length < minLengthToRevealTail)
        {
            return "••••••••";
        }

        return "••••••••" + secret[^4..];
    }

    public static string Render(EdgeAdminViewModel model)
    {
        var maskedLineSecret = MaskSecret(model.LineChannelSecret);
        var maskedLineToken = MaskSecret(model.LineChannelAccessToken);
        var maskedIngestKey = MaskSecret(model.IngestApiKey);

        var ingestIpsText = model.IngestAllowedClientIps is not null
            ? string.Join("\n", model.IngestAllowedClientIps)
            : "";

        var webhookMode = string.Equals(model.WebhookSourceMode, "AllowlistOnly", StringComparison.OrdinalIgnoreCase)
            ? "AllowlistOnly"
            : "Any";

        var webhookIpsText = model.WebhookSourceAllowedIps is not null
            ? string.Join("\n", model.WebhookSourceAllowedIps)
            : "";

        var successAlertHtml = model.Saved
            ? """<div class="alert-success">設定已儲存，並立即生效。</div>"""
            : "";

        var anySelected = webhookMode == "Any" ? " selected" : "";
        var allowlistSelected = webhookMode == "AllowlistOnly" ? " selected" : "";

        return $$"""
<!DOCTYPE html>
<html lang="zh-TW">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Edge 管理設定</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            background-color: #f8f9fa;
            color: #212529;
            margin: 0;
            padding: 24px;
            display: flex;
            justify-content: center;
        }
        .container {
            background: #ffffff;
            border: 1px solid #dee2e6;
            border-radius: 8px;
            max-width: 680px;
            width: 100%;
            padding: 32px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.05);
        }
        h1 {
            font-size: 1.5rem;
            margin-top: 0;
            margin-bottom: 8px;
            color: #343a40;
        }
        .note {
            font-size: 0.875rem;
            color: #6c757d;
            margin-bottom: 24px;
        }
        .alert-success {
            background-color: #d4edda;
            color: #155724;
            border: 1px solid #c3e6cb;
            padding: 12px 16px;
            border-radius: 4px;
            margin-bottom: 20px;
            font-size: 0.95rem;
        }
        .form-group {
            margin-bottom: 20px;
        }
        label {
            display: block;
            font-weight: 600;
            margin-bottom: 6px;
            font-size: 0.9rem;
        }
        .field-desc {
            font-size: 0.8rem;
            color: #6c757d;
            margin-top: 4px;
        }
        input[type="password"],
        input[type="text"],
        select,
        textarea {
            width: 100%;
            box-sizing: border-box;
            padding: 8px 12px;
            font-size: 0.95rem;
            border: 1px solid #ced4da;
            border-radius: 4px;
            background-color: #fff;
        }
        input[type="password"]:focus,
        input[type="text"]:focus,
        select:focus,
        textarea:focus {
            outline: none;
            border-color: #80bdff;
            box-shadow: 0 0 0 0.2rem rgba(0,123,255,0.25);
        }
        textarea {
            resize: vertical;
            min-height: 80px;
            font-family: monospace;
        }
        .btn-submit {
            background-color: #28a745;
            color: #ffffff;
            border: none;
            padding: 10px 24px;
            font-size: 1rem;
            font-weight: 600;
            border-radius: 4px;
            cursor: pointer;
            width: 100%;
            margin-top: 12px;
        }
        .btn-submit:hover {
            background-color: #218838;
        }
    </style>
</head>
<body>
<div class="container">
    <h1>Edge 管理設定</h1>
    <div class="note">儲存後立即生效，不需重啟站台。機密欄位留空表示維持原值不變。</div>
    {{successAlertHtml}}
    <form method="post" action="/edge-admin">
        <div class="form-group">
            <label for="lineChannelSecret">LINE Channel Secret</label>
            <input type="password" id="lineChannelSecret" name="lineChannelSecret" placeholder="{{(string.IsNullOrEmpty(model.LineChannelSecret) ? "未設定" : WebUtility.HtmlEncode(maskedLineSecret))}}" />
            <div class="field-desc">留空＝維持原值</div>
        </div>
        <div class="form-group">
            <label for="lineChannelAccessToken">LINE Channel Access Token</label>
            <input type="password" id="lineChannelAccessToken" name="lineChannelAccessToken" placeholder="{{(string.IsNullOrEmpty(model.LineChannelAccessToken) ? "未設定" : WebUtility.HtmlEncode(maskedLineToken))}}" />
            <div class="field-desc">留空＝維持原值</div>
        </div>
        <div class="form-group">
            <label for="ingestApiKey">Ingest 共用金鑰</label>
            <input type="password" id="ingestApiKey" name="ingestApiKey" placeholder="{{(string.IsNullOrEmpty(model.IngestApiKey) ? "未設定" : WebUtility.HtmlEncode(maskedIngestKey))}}" />
            <div class="field-desc">留空＝維持原值</div>
        </div>
        <div class="form-group">
            <label for="ingestAllowedClientIps">Ingest 允許來源 IP</label>
            <textarea id="ingestAllowedClientIps" name="ingestAllowedClientIps" rows="3" placeholder="每行一筆 IP 或 CIDR">{{WebUtility.HtmlEncode(ingestIpsText)}}</textarea>
            <div class="field-desc">多行文字，每行一筆 IP 或 CIDR 網段</div>
        </div>
        <div class="form-group">
            <label for="webhookSourceMode">Webhook 來源限制</label>
            <select id="webhookSourceMode" name="webhookSourceMode">
                <option value="Any"{{anySelected}}>Any（不限制來源）</option>
                <option value="AllowlistOnly"{{allowlistSelected}}>AllowlistOnly（僅允許白名單）</option>
            </select>
        </div>
        <div class="form-group">
            <label for="webhookSourceAllowedIps">Webhook 允許來源 IP</label>
            <textarea id="webhookSourceAllowedIps" name="webhookSourceAllowedIps" rows="3" placeholder="每行一筆 IP 或 CIDR">{{WebUtility.HtmlEncode(webhookIpsText)}}</textarea>
            <div class="field-desc">多行文字，每行一筆 IP 或 CIDR 網段</div>
        </div>
        <button type="submit" class="btn-submit">儲存設定</button>
    </form>
</div>
</body>
</html>
""";
    }
}
