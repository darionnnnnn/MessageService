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
    public void Line_AlwaysThrows_BecauseStage2NotImplemented()
    {
        // Stage 1 範圍：Line 模式的 ingest API 客戶端（HttpIngestSink）還沒實作，
        // 就算把該給的設定都給齊了也應該啟動失敗，而不是悄悄跑起來累積永遠排不空的 outbox
        Assert.Throws<InvalidOperationException>(() =>
            Validate(DeploymentMode.Line, new LineOptions { ChannelSecret = "secret" },
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
