using MessageService.Web.Configuration;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

public class EdgeAdminPageTests
{
    [Fact]
    public void Render_ConfiguredSecrets_ShowMaskAndLast4_FullSecretNeverAppears()
    {
        const string lineSecret = "super-secret-line-channel-1234";
        const string lineToken = "super-secret-line-token-5678";
        const string ingestKey = "super-secret-ingest-key-9999";

        var model = new EdgeAdminViewModel(
            LineChannelSecret: lineSecret,
            LineChannelAccessToken: lineToken,
            IngestApiKey: ingestKey,
            IngestAllowedClientIps: ["192.0.2.1"],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: ["192.0.2.2"]);

        var html = EdgeAdminPage.Render(model);

        // 完整值永遠不出現在輸出中
        Assert.DoesNotContain(lineSecret, html);
        Assert.DoesNotContain(lineToken, html);
        Assert.DoesNotContain(ingestKey, html);

        // 已設定的機密只出現遮罩與末四碼
        Assert.Contains("••••••••1234", html);
        Assert.Contains("••••••••5678", html);
        Assert.Contains("••••••••9999", html);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("xyz")]
    public void Render_SecretLengthLessThan4_DoesNotLeakAnyChar(string shortSecret)
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: shortSecret,
            LineChannelAccessToken: shortSecret,
            IngestApiKey: shortSecret,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: []);

        var html = EdgeAdminPage.Render(model);

        // 不得洩漏原始字元的任何明文，且 placeholder 全遮罩為 ••••••••
        Assert.Contains("placeholder=\"••••••••\"", html);
        Assert.DoesNotContain($"••••••••{shortSecret}", html);
        Assert.DoesNotContain($"placeholder=\"{shortSecret}\"", html);
        Assert.DoesNotContain($"value=\"{shortSecret}\"", html);
    }

    [Fact]
    public void Render_UnsetSecrets_ShowEmptyPlaceholder()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: "",
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: []);

        var html = EdgeAdminPage.Render(model);

        // 未設定的機密 placeholder 寫「未設定」
        Assert.Contains("placeholder=\"未設定\"", html);
        // 不應出現遮罩符號
        Assert.DoesNotContain("••••••••", html);
    }

    [Fact]
    public void Render_SpecialCharacters_AreHtmlEncoded()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: "<script>alert('secret')</script>",
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: ["192.0.2.1", "<script>alert('xss')</script>", "\"quoted\""],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: ["<b>bold</b>", "192.0.2.2"]);

        var html = EdgeAdminPage.Render(model);

        // 斷言輸出不含未逸出的 <script> 標籤
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);

        // 必須經過 HTML 逸出
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);
        Assert.Contains("&quot;quoted&quot;", html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", html);
    }

    [Fact]
    public void Render_ArrayFields_RenderedAsMultipleLines()
    {
        var ingestIps = new[] { "192.0.2.1", "192.0.2.2/24", "192.0.2.3" };
        var webhookIps = new[] { "192.0.2.10", "192.0.2.20/28" };

        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: ingestIps,
            WebhookSourceMode: "AllowlistOnly",
            WebhookSourceAllowedIps: webhookIps);

        var html = EdgeAdminPage.Render(model);

        // 多行呈現
        Assert.Contains("192.0.2.1\n192.0.2.2/24\n192.0.2.3", html);
        Assert.Contains("192.0.2.10\n192.0.2.20/28", html);
        Assert.Contains("<option value=\"AllowlistOnly\" selected>", html);
    }

    [Fact]
    public void Render_SavedFlag_ControlsSuccessAlert()
    {
        var modelSaved = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            Saved: true);

        var htmlSaved = EdgeAdminPage.Render(modelSaved);
        Assert.Contains("alert-success", htmlSaved);
        Assert.Contains("設定已儲存，並立即生效。", htmlSaved);

        var modelNotSaved = modelSaved with { Saved = false };
        var htmlNotSaved = EdgeAdminPage.Render(modelNotSaved);
        Assert.DoesNotContain("<div class=\"alert-success\">", htmlNotSaved);
        Assert.DoesNotContain("設定已儲存，並立即生效。", htmlNotSaved);
    }

    [Fact]
    public void Render_IsUnreadableFlag_ControlsDangerAlert()
    {
        var modelUnreadable = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            Saved: false,
            IsUnreadable: true);

        var htmlUnreadable = EdgeAdminPage.Render(modelUnreadable);
        Assert.Contains("<div class=\"alert-danger\">", htmlUnreadable);
        Assert.Contains("加密設定檔存在但無法解密", htmlUnreadable);

        var modelReadable = modelUnreadable with { IsUnreadable = false };
        var htmlReadable = EdgeAdminPage.Render(modelReadable);
        Assert.DoesNotContain("<div class=\"alert-danger\">", htmlReadable);
        Assert.DoesNotContain("加密設定檔存在但無法解密", htmlReadable);
    }

    [Theory]
    [InlineData("abcd")]            // 長度剛好 4：末四碼＝整串
    [InlineData("abcde")]
    [InlineData("abcdef1")]         // 5~7：露出一半以上
    public void MaskSecret_ShortSecrets_NeverRevealAnyCharacter(string secret)
    {
        var masked = EdgeAdminPage.MaskSecret(secret);

        // 遮罩是給人辨認「是不是我設的那把」，不是把金鑰放到畫面上
        Assert.Equal("••••••••", masked);
        Assert.DoesNotContain(secret, masked, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MaskSecret_LongEnoughSecret_RevealsOnlyLastFour()
    {
        var masked = EdgeAdminPage.MaskSecret("0123456789abcdef");

        Assert.Equal("••••••••cdef", masked);
        Assert.DoesNotContain("0123", masked, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Render_TabsStructure_ContainsAllThreeTabsAndContainers()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: []);

        var html = EdgeAdminPage.Render(model);

        // 分頁標籤與容器
        Assert.Contains("id=\"tab-label-settings\"", html);
        Assert.Contains("id=\"tab-label-connection\"", html);
        Assert.Contains("id=\"tab-label-troubleshooting\"", html);
        Assert.Contains("設定", html);
        Assert.Contains("連線測試", html);
        Assert.Contains("錯誤排查", html);

        Assert.Contains("id=\"tab-settings\"", html);
        Assert.Contains("id=\"tab-connection\"", html);
        Assert.Contains("id=\"tab-troubleshooting\"", html);

        // 錯誤排查三區塊標題
        Assert.Contains("本機最近錯誤", html);
        Assert.Contains("今日 log 檔尾", html);
        Assert.Contains("EdgeProxy 端錯誤", html);
    }

    [Fact]
    public void Render_Troubleshooting_WhenBufferHasEntries_RendersTableWithEscapedContent()
    {
        var timestamp = new DateTimeOffset(2026, 9, 1, 10, 30, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new MessageService.Web.Diagnostics.LogBufferEntry(
                TimestampUtc: timestamp,
                Level: Microsoft.Extensions.Logging.LogLevel.Error,
                Category: "MessageService.Test<Category>",
                Message: "Something failed with <script>alert('xss')</script>",
                ExceptionSummary: "InvalidOperationException: boom <script>")
        };

        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            LocalErrors: entries);

        var html = EdgeAdminPage.Render(model);

        // 表格標頭
        Assert.Contains("<th>時間</th>", html);
        Assert.Contains("<th>等級</th>", html);
        Assert.Contains("<th>分類</th>", html);
        Assert.Contains("<th>訊息</th>", html);
        Assert.Contains("<th>例外摘要</th>", html);

        // 等級與時間
        Assert.Contains("badge-error", html);
        Assert.Contains(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), html);

        // 逸出驗證：不得出現原樣 <script>
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);
        Assert.Contains("MessageService.Test&lt;Category&gt;", html);
        Assert.Contains("boom &lt;script&gt;", html);
    }

    [Fact]
    public void Render_Troubleshooting_WhenBufferEmpty_RendersEmptyBufferMessage()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            LocalErrors: []);

        var html = EdgeAdminPage.Render(model);

        Assert.Contains("目前沒有記錄到警告以上訊息", html);
    }

    [Fact]
    public void Render_Troubleshooting_LogTail_RendersContentOrErrorMessage()
    {
        // 情況一：有 log 內容且含特殊字元
        var modelWithLog = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            TodayLogContent: "2026-09-01 Log entry 1 <script>\n2026-09-01 Log entry 2",
            TodayLogErrorMessage: null);

        var htmlWithLog = EdgeAdminPage.Render(modelWithLog);
        Assert.Contains("<pre class=\"log-pre\">", htmlWithLog);
        Assert.Contains("2026-09-01 Log entry 1 &lt;script&gt;\n2026-09-01 Log entry 2", htmlWithLog);
        Assert.DoesNotContain("<script>", htmlWithLog);

        // 情況二：今天尚無 log 檔
        var modelNoLog = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            TodayLogContent: null,
            TodayLogErrorMessage: "今天尚無 log 檔");

        var htmlNoLog = EdgeAdminPage.Render(modelNoLog);
        Assert.Contains("今天尚無 log 檔", htmlNoLog);

        // 情況三：讀檔失敗
        var modelErrLog = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            TodayLogContent: null,
            TodayLogErrorMessage: "無法讀取 log 檔：存取被拒");

        var htmlErrLog = EdgeAdminPage.Render(modelErrLog);
        Assert.Contains("無法讀取 log 檔：存取被拒", htmlErrLog);
    }

    [Fact]
    public void Render_Troubleshooting_ProxyErrors_RendersStatusOrTable()
    {
        // 情況一：未使用 EdgeProxy
        var modelUnused = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            ProxyStatusMessage: "本主機未使用 EdgeProxy");

        var htmlUnused = EdgeAdminPage.Render(modelUnused);
        Assert.Contains("本主機未使用 EdgeProxy", htmlUnused);

        // 情況二：連線失敗訊息
        var modelFailed = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            ProxyStatusMessage: "無法連上 EdgeProxy：連線逾時——請直接查看該主機的 logs 目錄");

        var htmlFailed = EdgeAdminPage.Render(modelFailed);
        Assert.Contains("無法連上 EdgeProxy：連線逾時——請直接查看該主機的 logs 目錄", htmlFailed);

        // 情況三：連線成功且有條目（與本機錯誤共用表格渲染）
        var entries = new[]
        {
            new MessageService.Web.Diagnostics.LogBufferEntry(
                TimestampUtc: new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
                Level: Microsoft.Extensions.Logging.LogLevel.Warning,
                Category: "EdgeProxy.Forwarder",
                Message: "Proxy warning message",
                ExceptionSummary: null)
        };

        var modelProxyOk = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            ProxyErrors: entries,
            ProxyStatusMessage: null);

        var htmlProxyOk = EdgeAdminPage.Render(modelProxyOk);
        Assert.Contains("badge-warning", htmlProxyOk);
        Assert.Contains("EdgeProxy.Forwarder", htmlProxyOk);
        Assert.Contains("Proxy warning message", htmlProxyOk);
    }

    [Fact]
    public void Render_ConnectionTab_WhenOutboundHereFalse_DisplaysDisabledMessageAndNoButtons()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            OutboundHere: false);

        var html = EdgeAdminPage.Render(model);

        Assert.Contains("此主機未啟用 LINE outbound，無法測試", html);
        Assert.DoesNotContain("測試目前生效的 Token", html);
        Assert.DoesNotContain("用這個 Token 測試（不儲存）", html);
    }

    [Fact]
    public void Render_ConnectionTab_WhenTestResultSuccess_DisplaysGreenAlertAndEscapesBotName()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            OutboundHere: true,
            LineTestResult: new MessageService.Web.Services.LineConnectivityTestResult(
                Success: true,
                BotDisplayName: "我的 <script>Bot</script>",
                ErrorMessage: null,
                Via: "Direct"),
            ActiveTab: "connection");

        var html = EdgeAdminPage.Render(model);

        Assert.Contains("alert-success", html);
        Assert.Contains("連線成功：我的 &lt;script&gt;Bot&lt;/script&gt;（經由 Direct）", html);
        Assert.DoesNotContain("<script>Bot</script>", html);
        Assert.Contains("id=\"tab-nav-connection\" name=\"admin-tab\" class=\"tab-radio\" checked", html);
    }

    [Fact]
    public void Render_ConnectionTab_WhenTestResultFailed_DisplaysRedAlertAndEscapesError()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: [],
            OutboundHere: true,
            LineTestResult: new MessageService.Web.Services.LineConnectivityTestResult(
                Success: false,
                BotDisplayName: null,
                ErrorMessage: "HTTP 401 <b>Unauthorized</b>",
                Via: "EdgeProxy(https://proxy.example/MSLine/)"),
            ActiveTab: "connection");

        var html = EdgeAdminPage.Render(model);

        Assert.Contains("alert-danger", html);
        Assert.Contains("連線失敗：HTTP 401 &lt;b&gt;Unauthorized&lt;/b&gt;（經由 EdgeProxy(https://proxy.example/MSLine/)）", html);
        Assert.DoesNotContain("<b>Unauthorized</b>", html);
    }

    [Fact]
    public void Render_ActiveTab_DefaultIsSettings()
    {
        var model = new EdgeAdminViewModel(
            LineChannelSecret: null,
            LineChannelAccessToken: null,
            IngestApiKey: null,
            IngestAllowedClientIps: [],
            WebhookSourceMode: "Any",
            WebhookSourceAllowedIps: []);

        var html = EdgeAdminPage.Render(model);

        Assert.Contains("id=\"tab-nav-settings\" name=\"admin-tab\" class=\"tab-radio\" checked", html);
        Assert.Contains("id=\"tab-nav-connection\" name=\"admin-tab\" class=\"tab-radio\" />", html);
        Assert.Contains("id=\"tab-nav-troubleshooting\" name=\"admin-tab\" class=\"tab-radio\" />", html);
    }
}

