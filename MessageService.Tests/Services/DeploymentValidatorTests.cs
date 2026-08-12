using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

public class DeploymentValidatorTests
{
    private static void Validate(DeploymentMode mode, LineOptions? line = null, IngestOptions? ingest = null) =>
        DeploymentValidator.Validate(
            new DeploymentOptions { Mode = mode },
            line ?? new LineOptions { ChannelSecret = "secret" },
            ingest ?? new IngestOptions(),
            NullLogger.Instance);

    [Fact]
    public void Full_WithChannelSecret_DoesNotThrow()
    {
        var ex = Record.Exception(() => Validate(DeploymentMode.Full, new LineOptions { ChannelSecret = "secret" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Full_WithoutChannelSecret_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(DeploymentMode.Full, new LineOptions { ChannelSecret = "" }));
    }

    [Fact]
    public void Line_WithBaseUrlAndApiKeyAndChannelSecret_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Line, new LineOptions { ChannelSecret = "secret" },
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "key" }));

        Assert.Null(ex);
    }

    [Fact]
    public void Line_WithoutBaseUrl_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, new LineOptions { ChannelSecret = "secret" },
                new IngestOptions { BaseUrl = "", ApiKey = "key" }));
    }

    [Fact]
    public void Line_WithoutApiKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, new LineOptions { ChannelSecret = "secret" },
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "" }));
    }

    [Fact]
    public void Line_WithoutChannelSecret_Throws()
    {
        // Line 模式仍然收 webhook，跟 Full 模式一樣需要簽章驗證用的 ChannelSecret——
        // 這條規則是既有的（Full_WithoutChannelSecret_Throws 已涵蓋 Full），這裡確認 Line 一致
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, new LineOptions { ChannelSecret = "" },
                new IngestOptions { BaseUrl = "https://db-host", ApiKey = "key" }));
    }

    [Fact]
    public void Db_WithApiKey_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Validate(DeploymentMode.Db, new LineOptions { ChannelSecret = "" }, new IngestOptions { ApiKey = "key" }));

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
            Validate(DeploymentMode.Db, new LineOptions { ChannelSecret = "" }, new IngestOptions { ApiKey = "key" }));

        Assert.Null(ex);
    }
}
