using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

public class DeploymentValidatorTests
{
    // OutboundHere 預設 true（LineOptions 的類別預設值），這裡測試的重點大多不是
    // OutboundHere 本身，所以預設 LineOptions 一律關掉它並補上一個測試用的
    // ChannelAccessToken，避免每個不相關的測試都要自己顧到這條新規則。
    // 專門測 OutboundHere 邏輯的案例在檔案下半部，會自己明確設定。
    private static LineOptions Line(string channelSecret = "secret", bool outboundHere = false, string? channelAccessToken = null) =>
        new()
        {
            ChannelSecret = channelSecret,
            OutboundHere = outboundHere,
            ChannelAccessToken = channelAccessToken ?? (outboundHere ? "token" : "")
        };

    private static void Validate(DeploymentMode mode, LineOptions? line = null, IngestOptions? ingest = null) =>
        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode },
            line ?? Line(),
            ingest ?? new IngestOptions(),
            NullLogger.Instance);

    [Fact]
    public void Full_WithChannelSecret_DoesNotThrow()
    {
        var ex = Record.Exception(() => Validate(DeploymentMode.Full, Line(channelSecret: "secret")));

        Assert.Null(ex);
    }

    [Fact]
    public void Full_WithoutChannelSecret_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(DeploymentMode.Full, Line(channelSecret: "")));
    }

    [Fact]
    public void Line_WithBaseUrlAndApiKeyAndChannelSecret_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Line, Line(channelSecret: "secret"),
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "key" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Line_WithoutBaseUrl_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, Line(channelSecret: "secret"),
                new IngestOptions { BaseUrl = "", ApiKey = "key" }));
    }

    [Fact]
    public void Line_WithoutApiKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, Line(channelSecret: "secret"),
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "" }));
    }

    [Fact]
    public void Line_WithoutChannelSecret_Throws()
    {
        // Line 模式仍然收 webhook，跟 Full 模式一樣需要簽章驗證用的 ChannelSecret
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, Line(channelSecret: ""),
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "key" }));
    }

    [Fact]
    public void Db_WithApiKey_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Db, Line(channelSecret: ""), new IngestOptions { ApiKey = "key" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Db_WithoutApiKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(DeploymentMode.Db, ingest: new IngestOptions { ApiKey = "" }));
    }

    [Fact]
    public void Db_DoesNotRequireChannelSecret()
    {
        // Db 模式不收 webhook，不該要求 Line:ChannelSecret
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Db, Line(channelSecret: ""), new IngestOptions { ApiKey = "key" }));

        Assert.Null(ex);
    }

    // ==== Stage 3：Line:OutboundHere 的驗證 ====

    [Fact]
    public void OutboundHereTrue_WithChannelAccessToken_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Full, Line(outboundHere: true, channelAccessToken: "token")));

        Assert.Null(ex);
    }

    [Fact]
    public void OutboundHereTrue_WithoutChannelAccessToken_Throws()
    {
        // OutboundHere=true 卻沒有 ChannelAccessToken 不會啟動就爆炸——是背景服務起來後
        // 才悄悄一直打 401，所以要在啟動關卡擋下來
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Full, Line(outboundHere: true, channelAccessToken: "")));

        Assert.Contains("ChannelAccessToken", ex.Message);
    }

    [Fact]
    public void OutboundHereFalse_DoesNotRequireChannelAccessToken()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Full, Line(outboundHere: false, channelAccessToken: "")));

        Assert.Null(ex);
    }

    [Fact]
    public void FullMode_OutboundHereFalse_DoesNotThrow_OnlyWarns()
    {
        // 單機部署關掉媒體下載是可疑組合但不是錯誤——只記警告，不擋啟動
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Full, Line(outboundHere: false)));

        Assert.Null(ex);
    }

    [Fact]
    public void LineMode_OutboundHereFalse_DoesNotWarn_NotFullSpecificRule()
    {
        // Full+OutboundHere=false 的警告是 Full 模式專屬的可疑組合檢查；Line 模式關掉
        // OutboundHere 是常見且合理的拆機設定（媒體交給 Db 端或另一台負責），不該被當成可疑
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Line, Line(outboundHere: false),
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "key" }));

        Assert.Null(ex);
    }
}
