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

    private static void Validate(DeploymentMode mode, LineOptions? line = null, IngestOptions? ingest = null, ViewerOptions? viewer = null) =>
        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode },
            line ?? Line(),
            viewer ?? new ViewerOptions(),
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

    // ==== Stage 2：新舊模式名稱等價、Viewer 模式、Core+檢視端組合 ====

    [Fact]
    public void CanonicalNames_ProduceIdenticalOutcomeToLegacyNames()
    {
        // DeploymentMode.Full/Line/Db 是 AllInOne/Edge/Core 的別名（同一個底層數值），
        // 用新名稱寫的呼叫應該跟舊名稱完全同結果——這裡直接拿舊測試案例的輸入用新名稱重跑一次
        var exAllInOne = Record.Exception(() => Validate(DeploymentMode.AllInOne, Line(channelSecret: "secret")));
        var exEdge = Record.Exception(() =>
            Validate(DeploymentMode.Edge, Line(channelSecret: "secret"),
                new IngestOptions { BaseUrl = "https://core-host", ApiKey = "key" }));
        var exCore = Record.Exception(() =>
            Validate(DeploymentMode.Core, Line(channelSecret: ""), new IngestOptions { ApiKey = "key" }));

        Assert.Null(exAllInOne);
        Assert.Null(exEdge);
        Assert.Null(exCore);
    }

    [Fact]
    public void ViewerMode_WithNoLineOrIngestConfig_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Viewer, Line(channelSecret: "", outboundHere: false),
                new IngestOptions()));

        Assert.Null(ex);
    }

    [Fact]
    public void ViewerMode_WithLeftoverLineChannelSecret_DoesNotThrow_OnlyWarns()
    {
        // 從其他主機複製 appsettings 忘記清掉 Line:ChannelSecret 是可疑組合，不是錯誤——
        // Viewer 模式根本不會用到這個值
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Viewer, Line(channelSecret: "leftover-secret", outboundHere: false),
                new IngestOptions()));

        Assert.Null(ex);
    }

    [Fact]
    public void CoreMode_ViewerEnabledWithEmptyAllowlist_DoesNotThrow_OnlyWarns()
    {
        // Core 模式預設一併開檢視端；空白名單雖然是「全拒」而非啟動失敗，仍值得一則警告
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Core, Line(channelSecret: ""),
                new IngestOptions { ApiKey = "key" }, new ViewerOptions { AllowedClientIps = [] }));

        Assert.Null(ex);
    }

    [Fact]
    public void CoreMode_ViewerExplicitlyDisabled_DoesNotThrow_EvenWithEmptyAllowlist()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Core, Line(channelSecret: ""),
                new IngestOptions { ApiKey = "key" }, new ViewerOptions { Enabled = false, AllowedClientIps = [] }));

        Assert.Null(ex);
    }
}
