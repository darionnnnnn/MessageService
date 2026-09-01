using System.Net;
using System.Text;
using MessageService.Web.Diagnostics;
using MessageService.Web.Services;
using Microsoft.Extensions.Logging;

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
    bool Saved = false,
    bool IsUnreadable = false,
    IReadOnlyList<LogBufferEntry>? LocalErrors = null,
    string? TodayLogContent = null,
    string? TodayLogErrorMessage = null,
    IReadOnlyList<LogBufferEntry>? ProxyErrors = null,
    string? ProxyStatusMessage = null,
    bool OutboundHere = true,
    IReadOnlyList<LineConnectivityTestResult>? LineTestResults = null,
    string? ActiveTab = null);

/// <summary>
/// 提供純函式產生 Edge 端管理設定頁 HTML。
/// 不依賴任何外部 CSS/JS/字型，整頁自包含，所有動態文字皆做 HTML 逸出。
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

    /// <summary>
    /// 產生日誌緩衝條目表格 HTML。本機最近錯誤（區塊一）與 EdgeProxy 端錯誤（區塊三）共用此函式。
    /// </summary>
    public static string RenderLogEntriesTable(IReadOnlyList<LogBufferEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return """<div class="empty-msg">目前沒有記錄到警告以上訊息</div>""";
        }

        var sb = new StringBuilder();
        sb.Append("""
<div class="table-responsive">
    <table>
        <thead>
            <tr>
                <th>時間</th>
                <th>等級</th>
                <th>分類</th>
                <th>訊息</th>
                <th>例外摘要</th>
            </tr>
        </thead>
        <tbody>
""");

        foreach (var entry in entries)
        {
            var timeStr = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var levelStr = entry.Level.ToString();
            var levelClass = entry.Level switch
            {
                LogLevel.Warning => "badge-warning",
                LogLevel.Error => "badge-error",
                LogLevel.Critical => "badge-critical",
                _ => "badge-info"
            };
            var categoryEncoded = WebUtility.HtmlEncode(entry.Category);
            var messageEncoded = WebUtility.HtmlEncode(entry.Message);
            var exceptionEncoded = string.IsNullOrEmpty(entry.ExceptionSummary)
                ? "-"
                : $"<pre class=\"exception-pre\">{WebUtility.HtmlEncode(entry.ExceptionSummary)}</pre>";

            sb.Append($"""
            <tr>
                <td class="col-time">{timeStr}</td>
                <td><span class="badge {levelClass}">{levelStr}</span></td>
                <td class="col-category">{categoryEncoded}</td>
                <td class="col-message">{messageEncoded}</td>
                <td class="col-exception">{exceptionEncoded}</td>
            </tr>
""");
        }

        sb.Append("""
        </tbody>
    </table>
</div>
""");

        return sb.ToString();
    }

    public static string RenderTodayLog(string? content, string? errorMessage)
    {
        if (!string.IsNullOrEmpty(errorMessage))
        {
            return $"""<div class="status-msg">{WebUtility.HtmlEncode(errorMessage)}</div>""";
        }

        return $"""<pre class="log-pre">{WebUtility.HtmlEncode(content ?? "")}</pre>""";
    }

    public static string RenderProxyErrors(IReadOnlyList<LogBufferEntry>? entries, string? statusMessage)
    {
        // statusMessage 有兩種：拉不到時的原因（此時 entries 為 null，只顯示訊息），
        // 以及拉到時的來源主機名（此時訊息與表格一起顯示）
        var statusHtml = string.IsNullOrEmpty(statusMessage)
            ? ""
            : $"""<div class="status-msg">{WebUtility.HtmlEncode(statusMessage)}</div>""";

        return entries is null ? statusHtml : statusHtml + RenderLogEntriesTable(entries);
    }

    public static string RenderConnectionTab(bool outboundHere, IReadOnlyList<LineConnectivityTestResult>? testResults)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <div id="tab-connection" class="tab-pane tab-pane-connection">
                <h2>連線測試</h2>
""");

        if (!outboundHere)
        {
            sb.Append("""
                <div class="status-msg">此主機未啟用 LINE outbound，無法測試</div>
            </div>
""");
            return sb.ToString();
        }

        if (testResults is not null && testResults.Count > 0)
        {
            var allSuccess = testResults.All(r => r.Success);
            if (allSuccess)
            {
                sb.Append("""
                <div class="alert-success">連線測試完成：全部連線正常</div>
""");
            }
            else
            {
                sb.Append("""
                <div class="alert-danger">連線測試完成：部分連線異常，請檢查下方表格</div>
""");
            }

            sb.Append(RenderConnectionTestResultsTable(testResults));
        }

        sb.Append("""
                <div class="note">測試會對 LINE 官方 <code>v2/bot/info</code> 等相關網域發出連線請求。</div>
                <form method="post" action="/edge-admin/test-line">
                    <button type="submit" class="btn-submit">測試目前生效的 Token</button>
                </form>
                <form method="post" action="/edge-admin/test-line" style="margin-top: 24px;">
                    <div class="form-group">
                        <label for="overrideToken">覆寫 Channel Access Token 測試</label>
                        <input type="password" id="overrideToken" name="overrideToken" placeholder="輸入要測試的 Token（留空不覆寫）" />
                        <div class="field-desc">僅用於本次連線測試，不會儲存至設定檔</div>
                    </div>
                    <button type="submit" class="btn-submit">用這個 Token 測試（不儲存）</button>
                </form>
            </div>
""");

        return sb.ToString();
    }

    public static string RenderConnectionTestResultsTable(IReadOnlyList<LineConnectivityTestResult>? results)
    {
        if (results is null || results.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("""
<div class="table-responsive" style="margin-bottom: 24px;">
    <table>
        <thead>
            <tr>
                <th>用途</th>
                <th>目標</th>
                <th>結果</th>
                <th>說明</th>
                <th>經由</th>
            </tr>
        </thead>
        <tbody>
""");

        foreach (var item in results)
        {
            var purposeEncoded = WebUtility.HtmlEncode(item.Purpose);
            var targetEncoded = WebUtility.HtmlEncode(item.Target);
            var viaEncoded = WebUtility.HtmlEncode(item.Via);
            var descEncoded = WebUtility.HtmlEncode(item.Description);

            var resultText = item.Success
                ? (item.StrictSuccess ? "成功" : "可達")
                : (item.StrictSuccess ? "失敗" : "不可達");

            var badgeClass = item.Success ? "badge-info" : "badge-error";

            sb.Append($"""
            <tr>
                <td>{purposeEncoded}</td>
                <td>{targetEncoded}</td>
                <td><span class="badge {badgeClass}">{resultText}</span></td>
                <td>{descEncoded}</td>
                <td>{viaEncoded}</td>
            </tr>
""");
        }

        sb.Append("""
        </tbody>
    </table>
</div>
""");

        return sb.ToString();
    }

    public static string Render(EdgeAdminViewModel model)
    {
        var isConnectionTab = string.Equals(model.ActiveTab, "connection", StringComparison.OrdinalIgnoreCase);
        var isTroubleshootingTab = string.Equals(model.ActiveTab, "troubleshooting", StringComparison.OrdinalIgnoreCase);
        var isSettingsTab = !isConnectionTab && !isTroubleshootingTab;

        var settingsChecked = isSettingsTab ? " checked" : "";
        var connectionChecked = isConnectionTab ? " checked" : "";
        var troubleshootingChecked = isTroubleshootingTab ? " checked" : "";

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

        var unreadableAlertHtml = model.IsUnreadable
            ? """<div class="alert-danger">加密設定檔存在但無法解密（常見原因：主機更換或還原映像）。目前生效的是 appsettings 的值，重新填寫並存檔即可重建。</div>"""
            : "";

        var successAlertHtml = model.Saved
            ? """<div class="alert-success">設定已儲存，並立即生效。</div>"""
            : "";

        var anySelected = webhookMode == "Any" ? " selected" : "";
        var allowlistSelected = webhookMode == "AllowlistOnly" ? " selected" : "";

        var localErrorsHtml = RenderLogEntriesTable(model.LocalErrors);
        var todayLogHtml = RenderTodayLog(model.TodayLogContent, model.TodayLogErrorMessage);
        var proxyErrorsHtml = RenderProxyErrors(model.ProxyErrors, model.ProxyStatusMessage);
        var connectionTabHtml = RenderConnectionTab(model.OutboundHere, model.LineTestResults);

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
            max-width: 860px;
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
        .tab-radio {
            display: none;
        }
        .tab-nav {
            display: flex;
            border-bottom: 2px solid #dee2e6;
            margin-bottom: 24px;
            gap: 8px;
        }
        .tab-button {
            display: inline-block;
            padding: 8px 16px;
            font-size: 1rem;
            font-weight: 600;
            color: #6c757d;
            cursor: pointer;
            border-bottom: 2px solid transparent;
            margin-bottom: -2px;
            user-select: none;
            text-decoration: none;
        }
        .tab-button:hover {
            color: #495057;
        }
        #tab-nav-settings:checked ~ .tab-nav #tab-label-settings,
        #tab-nav-connection:checked ~ .tab-nav #tab-label-connection,
        #tab-nav-troubleshooting:checked ~ .tab-nav #tab-label-troubleshooting {
            color: #007bff;
            border-bottom-color: #007bff;
        }
        .tab-pane {
            display: none;
        }
        #tab-nav-settings:checked ~ .tab-content .tab-pane-settings,
        #tab-nav-connection:checked ~ .tab-content .tab-pane-connection,
        #tab-nav-troubleshooting:checked ~ .tab-content .tab-pane-troubleshooting {
            display: block;
        }
        .section {
            margin-bottom: 32px;
        }
        .section:last-child {
            margin-bottom: 0;
        }
        h2 {
            font-size: 1.15rem;
            margin-top: 0;
            margin-bottom: 12px;
            color: #495057;
            border-bottom: 1px solid #e9ecef;
            padding-bottom: 6px;
        }
        .empty-msg,
        .status-msg {
            color: #6c757d;
            font-size: 0.9rem;
            padding: 12px 16px;
            background-color: #f8f9fa;
            border: 1px solid #e9ecef;
            border-radius: 4px;
        }
        .table-responsive {
            overflow-x: auto;
        }
        table {
            width: 100%;
            border-collapse: collapse;
            font-size: 0.85rem;
        }
        th, td {
            border: 1px solid #dee2e6;
            padding: 8px 10px;
            text-align: left;
            vertical-align: top;
        }
        th {
            background-color: #f8f9fa;
            font-weight: 600;
            color: #495057;
        }
        .col-time {
            white-space: nowrap;
            width: 150px;
        }
        .col-category {
            word-break: break-word;
            max-width: 180px;
        }
        .col-message {
            word-break: break-word;
        }
        .col-exception {
            max-width: 250px;
        }
        .badge {
            display: inline-block;
            padding: 2px 6px;
            border-radius: 3px;
            font-size: 0.75rem;
            font-weight: 600;
        }
        .badge-warning {
            color: #856404;
            background-color: #fff3cd;
        }
        .badge-error {
            color: #721c24;
            background-color: #f8d7da;
        }
        .badge-critical {
            color: #ffffff;
            background-color: #dc3545;
        }
        .badge-info {
            color: #0c5460;
            background-color: #d1ecf1;
        }
        .exception-pre {
            margin: 0;
            font-family: Consolas, Monaco, "Courier New", monospace;
            font-size: 0.8rem;
            white-space: pre-wrap;
            word-break: break-all;
            max-height: 150px;
            overflow-y: auto;
        }
        .log-pre {
            background-color: #f8f9fa;
            border: 1px solid #dee2e6;
            border-radius: 4px;
            padding: 12px;
            font-family: Consolas, Monaco, "Courier New", monospace;
            font-size: 0.825rem;
            line-height: 1.4;
            white-space: pre-wrap;
            word-break: break-all;
            max-height: 400px;
            overflow-y: auto;
            margin: 0;
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
        .alert-danger {
            background-color: #f8d7da;
            color: #721c24;
            border: 1px solid #f5c6cb;
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

    <div class="tabs-container">
        <input type="radio" id="tab-nav-settings" name="admin-tab" class="tab-radio"{{settingsChecked}} />
        <input type="radio" id="tab-nav-connection" name="admin-tab" class="tab-radio"{{connectionChecked}} />
        <input type="radio" id="tab-nav-troubleshooting" name="admin-tab" class="tab-radio"{{troubleshootingChecked}} />

        <div class="tab-nav">
            <label for="tab-nav-settings" class="tab-button" id="tab-label-settings">設定</label>
            <label for="tab-nav-connection" class="tab-button" id="tab-label-connection">連線測試</label>
            <label for="tab-nav-troubleshooting" class="tab-button" id="tab-label-troubleshooting">錯誤排查</label>
        </div>

        <div class="tab-content">
            <div id="tab-settings" class="tab-pane tab-pane-settings">
                {{unreadableAlertHtml}}
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
            {{connectionTabHtml}}
            <div id="tab-troubleshooting" class="tab-pane tab-pane-troubleshooting">
                <div class="section">
                    <h2>本機最近錯誤</h2>
                    {{localErrorsHtml}}
                </div>
                <div class="section">
                    <h2>今日 log 檔尾</h2>
                    {{todayLogHtml}}
                </div>
                <div class="section">
                    <h2>EdgeProxy 端錯誤</h2>
                    {{proxyErrorsHtml}}
                </div>
            </div>
        </div>
    </div>
</div>
</body>
</html>
""";
    }
}
