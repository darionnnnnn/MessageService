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
}
