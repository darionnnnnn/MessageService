using MessageService.Options;
using MessageService.Services;

namespace MessageService.Tests.Services;

// 這是全站唯一的能力推導點（見 DeploymentCapabilities 說明），這裡把四種模式 × override
// 的組合逐一釘住，避免日後改一處推導邏輯漏改另一處的回歸。
public class DeploymentCapabilitiesTests
{
    private static LineOptions Line(bool? outboundHere = null) => new() { OutboundHere = outboundHere };
    private static ViewerOptions Viewer(bool? enabled = null) => new() { Enabled = enabled };
    private static IngestOptions Ingest(string? apiKey = null) => new() { ApiKey = apiKey };

    [Fact]
    public void AllInOne_DefaultOverrides_HasAllExpectedCapabilities()
    {
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.AllInOne, Line(), Viewer(), Ingest());

        Assert.True(capabilities.ReceivesWebhook);
        Assert.True(capabilities.HasDatabaseAccess);
        Assert.False(capabilities.IngestApiEnabled); // 沒設 ApiKey
        Assert.True(capabilities.ViewerEnabled);
        Assert.True(capabilities.OutboundHere);
        Assert.True(capabilities.RunsRetention);
    }

    [Fact]
    public void AllInOne_WithApiKey_IngestApiEnabled()
    {
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.AllInOne, Line(), Viewer(), Ingest("key"));

        Assert.True(capabilities.IngestApiEnabled);
    }

    [Fact]
    public void Edge_DefaultOverrides_OnlyWebhookAndOutbound()
    {
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Edge, Line(), Viewer(), Ingest("key"));

        Assert.True(capabilities.ReceivesWebhook);
        Assert.False(capabilities.HasDatabaseAccess);
        Assert.False(capabilities.IngestApiEnabled); // Edge 從不暴露 ingest API，即使 ApiKey 有設
        Assert.False(capabilities.ViewerEnabled);
        Assert.True(capabilities.OutboundHere);
        Assert.False(capabilities.RunsRetention);
    }

    [Fact]
    public void Core_DefaultOverrides_DatabaseAndViewerButNoWebhookOrOutbound()
    {
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Core, Line(), Viewer(), Ingest("key"));

        Assert.False(capabilities.ReceivesWebhook);
        Assert.True(capabilities.HasDatabaseAccess);
        Assert.True(capabilities.IngestApiEnabled);
        Assert.True(capabilities.ViewerEnabled); // 預設一併開檢視端
        Assert.False(capabilities.OutboundHere);
        Assert.True(capabilities.RunsRetention);
    }

    [Fact]
    public void Core_ViewerExplicitlyDisabled_ViewerEnabledFalse()
    {
        // 三台拓撲：Core 專職資料庫＋ingest，檢視端另外一台負責
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Core, Line(), Viewer(enabled: false), Ingest("key"));

        Assert.False(capabilities.ViewerEnabled);
        // 關掉檢視端不影響其他能力
        Assert.True(capabilities.HasDatabaseAccess);
        Assert.True(capabilities.IngestApiEnabled);
        Assert.True(capabilities.RunsRetention);
    }

    [Fact]
    public void Viewer_DefaultOverrides_OnlyDatabaseAndViewer()
    {
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Viewer, Line(), Viewer(), Ingest("key"));

        Assert.False(capabilities.ReceivesWebhook);
        Assert.True(capabilities.HasDatabaseAccess);
        Assert.False(capabilities.IngestApiEnabled); // Viewer 模式即使給了 ApiKey 也不暴露 ingest API
        Assert.True(capabilities.ViewerEnabled);
        Assert.False(capabilities.OutboundHere);
        Assert.False(capabilities.RunsRetention); // 不跟 Core 搶著清同一張表
    }

    [Fact]
    public void Edge_ViewerExplicitlyEnabled_IsClampedToFalse()
    {
        // Edge 沒有 MessageDbContext，檢視端整組服務開不起來——顯式 override 若照單全收，
        // 服務註冊矩陣會註冊出解析不了的相依、炸出難懂的 DI 錯誤。推導端夾住讓註冊矩陣
        // 保持一致；「這個設定不會生效」的人話錯誤由 DeploymentValidator 在啟動時擋下
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Edge, Line(), Viewer(enabled: true), Ingest("key"));

        Assert.False(capabilities.ViewerEnabled);
    }

    [Fact]
    public void Viewer_ExplicitlyDisabled_IsAnInvalidButNonCrashingConfiguration()
    {
        // 沒有實際部署理由會這樣設（Viewer 模式關掉檢視端等於什麼都不做），但推導邏輯本身
        // 不該對這個組合有特殊行為——純粹尊重 override
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.Viewer, Line(), Viewer(enabled: false), Ingest());

        Assert.False(capabilities.ViewerEnabled);
    }

    [Theory]
    [InlineData(DeploymentMode.AllInOne, true)]
    [InlineData(DeploymentMode.Edge, true)]
    [InlineData(DeploymentMode.Core, false)]
    [InlineData(DeploymentMode.Viewer, false)]
    public void OutboundHere_Override_WinsOverModeDefault(DeploymentMode mode, bool modeDefault)
    {
        var withoutOverride = DeploymentCapabilities.Derive(mode, Line(), Viewer(), Ingest());
        var explicitTrue = DeploymentCapabilities.Derive(mode, Line(outboundHere: true), Viewer(), Ingest());
        var explicitFalse = DeploymentCapabilities.Derive(mode, Line(outboundHere: false), Viewer(), Ingest());

        Assert.Equal(modeDefault, withoutOverride.OutboundHere);
        Assert.True(explicitTrue.OutboundHere);
        Assert.False(explicitFalse.OutboundHere);
    }

    [Fact]
    public void LegacyModeNames_DeriveIdenticalCapabilitiesToCanonicalNames()
    {
        // DeploymentMode.Full/Line/Db 與 AllInOne/Edge/Core 共用同一個底層數值，推導結果
        // 理當完全一致——這裡直接用 == 比較兩個 record 確認沒有任何欄位漂移
        Assert.Equal(
            DeploymentCapabilities.Derive(DeploymentMode.AllInOne, Line(), Viewer(), Ingest("key")),
            DeploymentCapabilities.Derive(DeploymentMode.Full, Line(), Viewer(), Ingest("key")));
        Assert.Equal(
            DeploymentCapabilities.Derive(DeploymentMode.Edge, Line(), Viewer(), Ingest("key")),
            DeploymentCapabilities.Derive(DeploymentMode.Line, Line(), Viewer(), Ingest("key")));
        Assert.Equal(
            DeploymentCapabilities.Derive(DeploymentMode.Core, Line(), Viewer(), Ingest("key")),
            DeploymentCapabilities.Derive(DeploymentMode.Db, Line(), Viewer(), Ingest("key")));
    }

    [Theory]
    [InlineData(DeploymentMode.AllInOne, IngestChannel.Auto, false)]
    [InlineData(DeploymentMode.AllInOne, IngestChannel.Push, false)]
    [InlineData(DeploymentMode.AllInOne, IngestChannel.Pull, false)]
    [InlineData(DeploymentMode.Edge, IngestChannel.Auto, true)]
    [InlineData(DeploymentMode.Edge, IngestChannel.Push, false)]
    [InlineData(DeploymentMode.Edge, IngestChannel.Pull, true)]
    [InlineData(DeploymentMode.Core, IngestChannel.Auto, false)]
    [InlineData(DeploymentMode.Core, IngestChannel.Push, false)]
    [InlineData(DeploymentMode.Core, IngestChannel.Pull, false)]
    [InlineData(DeploymentMode.Viewer, IngestChannel.Auto, false)]
    [InlineData(DeploymentMode.Viewer, IngestChannel.Push, false)]
    [InlineData(DeploymentMode.Viewer, IngestChannel.Pull, false)]
    public void EdgePullApiEnabled_DerivesCorrectlyForModeAndChannel(DeploymentMode mode, IngestChannel channel, bool expected)
    {
        var capabilities = DeploymentCapabilities.Derive(
            mode, Line(), Viewer(), new IngestOptions { Channel = channel, ApiKey = "key" });

        Assert.Equal(expected, capabilities.EdgePullApiEnabled);
    }

    [Fact]
    public void EdgePullApiEnabled_DoesNotDependOnApiKey()
    {
        // 刻意不檢查 ApiKey：金鑰未設時要讓 IngestApiKeyMiddleware 回 404，而不是讓路由消失後回 405
        var capabilitiesWithoutKey = DeploymentCapabilities.Derive(
            DeploymentMode.Edge, Line(), Viewer(), new IngestOptions { Channel = IngestChannel.Auto, ApiKey = null });

        Assert.True(capabilitiesWithoutKey.EdgePullApiEnabled);
    }
}
