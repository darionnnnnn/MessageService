using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

public class DeploymentValidatorTests
{
    /// <summary>Warning 類規則的驗證要看「真的有記」而不只是「沒丟例外」——NullLogger
    /// 驗不出後者與「整條規則被刪掉」的差別。</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }

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

    private static void Validate(
        DeploymentMode mode, LineOptions? line = null, IngestOptions? ingest = null,
        ViewerOptions? viewer = null, EdgeProxyOptions? edgeProxy = null) =>
        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode },
            line ?? Line(),
            viewer ?? new ViewerOptions(),
            ingest ?? new IngestOptions(),
            edgeProxy ?? new EdgeProxyOptions(),
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
    public void Edge_ChannelPull_WithApiKey_EmptyBaseUrl_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Edge, Line(channelSecret: "secret"),
                new IngestOptions { Channel = IngestChannel.Pull, BaseUrl = "", ApiKey = "key" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Edge_ChannelPull_WithoutApiKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Edge, Line(channelSecret: "secret"),
                new IngestOptions { Channel = IngestChannel.Pull, BaseUrl = "", ApiKey = "" }));

        Assert.Contains("Pull 模式仍需 ApiKey 驗證 Core 進來的輪詢請求", ex.Message);
    }

    [Fact]
    public void Edge_ChannelAuto_WithoutBaseUrl_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Edge, Line(channelSecret: "secret"),
                new IngestOptions { Channel = IngestChannel.Auto, BaseUrl = "", ApiKey = "key" }));

        Assert.Contains("Ingest:BaseUrl", ex.Message);
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

    [Theory]
    [InlineData(DeploymentMode.Core)]
    [InlineData(DeploymentMode.Viewer)]
    public void CoreOrViewer_ExplicitOutboundHereTrue_WarnsAboutDuplicateDownload(DeploymentMode mode)
    {
        // Core/Viewer 顯式開 OutboundHere 是合法的（把媒體下載搬離 Edge），但 Edge 端沒同步
        // 設 false 的話兩台會重複下載——跨主機組合錯誤單機驗證不出來，只能提醒
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode },
            Line(channelSecret: "", outboundHere: true, channelAccessToken: "token"),
            new ViewerOptions(),
            new IngestOptions { ApiKey = "key" },
            new EdgeProxyOptions(),
            logger);

        Assert.Contains(logger.Warnings, w => w.Contains("重複下載"));
    }

    [Fact]
    public void EdgeMode_ExplicitOutboundHereTrue_DoesNotWarnAboutDuplicateDownload()
    {
        // Edge 本來就是 OutboundHere 的預設歸屬，顯式設 true 只是把預設寫出來，不該被警告
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.Edge },
            Line(channelSecret: "secret", outboundHere: true, channelAccessToken: "token"),
            new ViewerOptions(),
            new IngestOptions { BaseUrl = "https://core-host", ApiKey = "key" },
            new EdgeProxyOptions(),
            logger);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("重複下載"));
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
    public void EdgeMode_ViewerExplicitlyEnabled_ThrowsWithClearMessage()
    {
        // Edge 沒有資料庫連線，Viewer:Enabled=true 不可能生效（capabilities 推導端會夾住）——
        // 但「以為檢視端有開、實際上沒有」比多餘設定嚴重，要在啟動關卡用人話擋下來
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Edge, Line(channelSecret: "secret"),
                new IngestOptions { BaseUrl = "https://core-host", ApiKey = "key" },
                new ViewerOptions { Enabled = true }));

        Assert.Contains("Viewer:Enabled", ex.Message);
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

    // AllInOne 是最常見的拓撲，空白名單同樣代表「檢視端啟用了卻全拒」——這條警告原本只在
    // Core 模式檢查，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次B
    [Fact]
    public void AllInOneMode_ViewerEnabledWithEmptyAllowlist_Warns()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = [] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger);

        Assert.Contains(logger.Warnings, w => w.Contains("AllowedClientIps"));
    }

    [Fact]
    public void AllInOneMode_ViewerEnabledWithNonEmptyAllowlist_DoesNotWarn()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("AllowedClientIps"));
    }

    // ==== Provider 與連線字串不一致 ====

    private static DatabaseStartupDecision Db(
        string? configuredProvider = null, string effectiveProvider = "Sqlite", bool wasInferred = true,
        bool hasSqlServerConnectionString = false, bool sqliteFallbackConfigured = false,
        bool sqliteFallbackEnabled = true, bool sqliteFallbackTriggered = false, string? sqliteFallbackReason = null) =>
        new(configuredProvider, effectiveProvider, wasInferred, hasSqlServerConnectionString,
            sqliteFallbackConfigured, sqliteFallbackEnabled, sqliteFallbackTriggered, sqliteFallbackReason);

    [Fact]
    public void ExplicitSqliteProvider_WithSqlServerConnectionString_Warns()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: "Sqlite", effectiveProvider: "Sqlite", wasInferred: false,
                hasSqlServerConnectionString: true));

        Assert.Contains(logger.Warnings, w => w.Contains("Database:Provider"));
    }

    [Fact]
    public void ExplicitSqliteProvider_WithoutSqlServerConnectionString_DoesNotWarn()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: "Sqlite", effectiveProvider: "Sqlite", wasInferred: false));

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Database:Provider"));
    }

    [Fact]
    public void ExplicitSqlServerProvider_WithSqlServerConnectionString_DoesNotWarn()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: "SqlServer", effectiveProvider: "SqlServer", wasInferred: false,
                hasSqlServerConnectionString: true));

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Database:Provider"));
    }

    [Fact]
    public void InferredSqlServerProvider_DoesNotWarn()
    {
        // 需求2：沒設 Provider、依連線字串推導成 SqlServer 是正常路徑，不是「殘留設定」，
        // 不該被當成 SqliteProvider_WithSqlServerConnectionString 那類警告
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: null, effectiveProvider: "SqlServer", wasInferred: true,
                hasSqlServerConnectionString: true));

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Database:Provider"));
    }

    [Fact]
    public void ExplicitSqlServerProvider_WithoutConnectionString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentValidator.Validate(
                new DeploymentOptions { Mode = DeploymentMode.AllInOne },
                Line(channelSecret: "secret"),
                new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
                new IngestOptions(),
                new EdgeProxyOptions(),
                NullLogger.Instance,
                Db(configuredProvider: "SqlServer", effectiveProvider: "SqlServer", wasInferred: false,
                    hasSqlServerConnectionString: false)));

        Assert.Contains("SqlServer", ex.Message);
    }

    [Theory]
    [InlineData(DeploymentMode.Core)]
    [InlineData(DeploymentMode.Edge)]
    [InlineData(DeploymentMode.Viewer)]
    public void NonAllInOneMode_WithSqliteFallbackConfigured_Warns(DeploymentMode mode)
    {
        var logger = new CapturingLogger();
        var line = mode is DeploymentMode.Edge
            ? Line(channelSecret: "secret", outboundHere: true, channelAccessToken: "token")
            : Line(channelSecret: "");
        var ingest = mode is DeploymentMode.Edge
            ? new IngestOptions { BaseUrl = "https://core-host", ApiKey = "key" }
            : new IngestOptions { ApiKey = "key" };

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode }, line, new ViewerOptions(), ingest,
            new EdgeProxyOptions(), logger,
            Db(sqliteFallbackConfigured: true));

        Assert.Contains(logger.Warnings, w => w.Contains("SqliteFallback"));
    }

    [Fact]
    public void AllInOneMode_WithSqliteFallbackConfigured_DoesNotWarn()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(sqliteFallbackConfigured: true));

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("SqliteFallback"));
    }

    [Fact]
    public void SqliteFallbackTriggered_LogsErrorWithReason()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: "SqlServer", effectiveProvider: "Sqlite", wasInferred: false,
                hasSqlServerConnectionString: true, sqliteFallbackTriggered: true,
                sqliteFallbackReason: "連線逾時"));

        Assert.Contains(logger.Errors, e => e.Contains("連線逾時"));
    }

    [Fact]
    public void SqliteFallbackNotTriggered_DoesNotLogError()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.AllInOne },
            Line(channelSecret: "secret"),
            new ViewerOptions { AllowedClientIps = ["10.0.0.0/24"] },
            new IngestOptions(),
            new EdgeProxyOptions(),
            logger,
            Db(configuredProvider: "SqlServer", effectiveProvider: "SqlServer", wasInferred: false,
                hasSqlServerConnectionString: true));

        Assert.Empty(logger.Errors);
    }

    [Fact]
    public void Core_WithEdgeBaseUrlButNoApiKey_Throws()
    {
        // 沒有金鑰的輪詢每一輪都會被 Edge 回 401，表現成「一直退避」很難查
        var ex = Assert.Throws<InvalidOperationException>(() => Validate(
            mode: DeploymentMode.Core,
            ingest: new IngestOptions { EdgeBaseUrl = "https://edge.example/", ApiKey = "" }));

        Assert.Contains("Ingest:ApiKey", ex.Message);
    }

    // ==== EdgeProxy 模式驗證 ====

    [Fact]
    public void EdgeProxy_WithTargetBaseUrl_DoesNotThrow()
    {
        var ex = Record.Exception(() => Validate(
            DeploymentMode.EdgeProxy,
            edgeProxy: new EdgeProxyOptions { TargetBaseUrl = "http://192.0.2.10/MSLine" }));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EdgeProxy_WithoutTargetBaseUrl_Throws(string? targetBaseUrl)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Validate(
            DeploymentMode.EdgeProxy,
            edgeProxy: new EdgeProxyOptions { TargetBaseUrl = targetBaseUrl }));

        Assert.Contains("EdgeProxy:TargetBaseUrl", ex.Message);
    }

    [Fact]
    public void EdgeProxy_WithLeftoverLineOrIngestOrViewerConfig_Warns()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.EdgeProxy },
            Line(channelSecret: "secret", outboundHere: false, channelAccessToken: "token"),
            new ViewerOptions { Enabled = true },
            new IngestOptions { BaseUrl = "https://core-host", ApiKey = "key", EdgeBaseUrl = "https://edge-host" },
            new EdgeProxyOptions { TargetBaseUrl = "http://192.0.2.10/MSLine" },
            logger);

        Assert.Contains(logger.Warnings, w => w.Contains("EdgeProxy 只做轉發"));
    }

    [Fact]
    public void EdgeProxy_CleanConfig_DoesNotWarn()
    {
        var logger = new CapturingLogger();

        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.EdgeProxy },
            new LineOptions { ChannelSecret = "", ChannelAccessToken = "", OutboundHere = null },
            new ViewerOptions { Enabled = null },
            new IngestOptions { BaseUrl = null, ApiKey = null, EdgeBaseUrl = null },
            new EdgeProxyOptions { TargetBaseUrl = "http://192.0.2.10/MSLine" },
            logger);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("EdgeProxy 只做轉發"));
    }

    [Fact]
    public void EdgeProxy_WithLeftoverDatabaseConnectionString_Warns()
    {
        var logger = new CapturingLogger();

        // 整份設定檔從 Core 複製過來時最常見的殘留——資料庫區塊對 EdgeProxy 完全沒有作用
        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.EdgeProxy },
            new LineOptions { ChannelSecret = "", ChannelAccessToken = "", OutboundHere = null },
            new ViewerOptions(),
            new IngestOptions(),
            new EdgeProxyOptions { TargetBaseUrl = "http://192.0.2.10/MSLine" },
            logger,
            DatabaseStartupDecision.Default with { HasSqlServerConnectionString = true });

        Assert.Contains(logger.Warnings, w => w.Contains("EdgeProxy 只做轉發"));
    }

    [Fact]
    public void EdgeProxy_WithFullCopiedEdgeConfig_DoesNotThrow_OnlyWarns()
    {
        var logger = new CapturingLogger();

        // 部署 EdgeProxy 最常見的做法就是整份 appsettings 從 Edge/Core 複製過來改 Mode——
        // 這些殘留（EdgeBaseUrl 缺 ApiKey、OutboundHere=true 缺 token、SqlServer 缺連線字串）
        // 都各自對到其他模式的擋啟動規則，EdgeProxy 必須在自己的檢查後直接返回，
        // 否則公網那台部署當天就起不來、錯誤訊息還指向它根本沒有的功能
        var ex = Record.Exception(() => DeploymentValidator.Validate(
            new DeploymentOptions { Mode = DeploymentMode.EdgeProxy },
            new LineOptions { ChannelSecret = "secret", ChannelAccessToken = "", OutboundHere = true },
            new ViewerOptions { Enabled = true },
            new IngestOptions { BaseUrl = "https://core-host", ApiKey = "", EdgeBaseUrl = "https://edge-host" },
            new EdgeProxyOptions { TargetBaseUrl = "http://192.0.2.10/MSLine" },
            logger,
            DatabaseStartupDecision.Default with
            {
                ConfiguredProvider = "SqlServer", EffectiveProvider = "SqlServer",
                HasSqlServerConnectionString = false,
            }));

        Assert.Null(ex);
        Assert.Contains(logger.Warnings, w => w.Contains("EdgeProxy 只做轉發"));
    }

    [Theory]
    [InlineData("192.0.2.10/MSLine")]
    [InlineData("ftp://edge-host/MSLine")]
    public void EdgeProxy_MalformedTargetBaseUrl_ThrowsAtStartup(string target)
    {
        // 漏打 http:// 這種手滑若不在啟動時擋，會變成每則 webhook 都 502、訊息靜默全掉
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.EdgeProxy, Line(channelSecret: ""),
                edgeProxy: new EdgeProxyOptions { TargetBaseUrl = target }));

        Assert.Contains("http", ex.Message);
    }
}
