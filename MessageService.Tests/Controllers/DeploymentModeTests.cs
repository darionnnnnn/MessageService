using System.Net;
using System.Net.Http.Json;
using System.Text;
using MessageService.Data;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Controllers;

// 用真實 host 驗證路由閘門、啟動驗證與 ingest API 的端到端行為——DeploymentModeConventionTests
// 只驗 convention 本身的行為，這裡驗「controller 從 application model 移除後，請求真的會
// 404、host 起得來、金鑰與 IP 白名單真的擋得住」這些依賴 MVC 路由與中介層管線內部行為的
// 最終結果（初版清 Selectors 的做法就是在這裡炸掉的）。
public class DeploymentModeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-mode-test-{Guid.NewGuid():N}.db");
    private readonly string _outboxPath = Path.Combine(Path.GetTempPath(), $"messageservice-mode-test-outbox-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _outboxPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder> configure, bool allowLocalhost = true)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            configure(builder);
            if (allowLocalhost)
            {
                builder.UseSetting("AllowedClientIps:0", "127.0.0.1");
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1"))));
            }
        });
    }

    [Fact]
    public async Task DbMode_WebhookEndpoint_DoesNotExist()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "test-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();

        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        // 是 404（路由不存在）而不是 401（存在但簽章不對）——Db 模式下這個端點應該「不存在」
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void LineMode_WithFullConfig_StartsSuccessfully()
    {
        // Stage 2：HttpIngestSink 與 ingest API 都已實作，給齊設定後 Line 模式應該起得來
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Ingest:BaseUrl", "https://db-host.example");
            builder.UseSetting("Ingest:ApiKey", "test-key");
        }, allowLocalhost: false);

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.Null(ex);
    }

    [Fact]
    public void LineMode_WithoutIngestConfig_FailsToStart()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
        }, allowLocalhost: false);

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Ingest:BaseUrl", ex.ToString());
    }

    [Fact]
    public async Task FullMode_WithoutIngestApiKey_IngestEndpointDoesNotExist_ButHostStartsFine()
    {
        // 預設單機部署（Full，沒特別設 Ingest:ApiKey）不該意外多開一個沒人保護的寫入端點——
        // 這是 RequiresIngestApiKeyAttribute 存在的理由
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Full");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxPath}");
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ingest/events", SampleEnvelope());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DbMode_IngestEvents_MissingKey_ReturnsUnauthorized()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ingest/events", SampleEnvelope());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DbMode_IngestEvents_WrongKey_ReturnsUnauthorized()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "wrong-key");

        var response = await client.PostAsJsonAsync("/api/ingest/events", SampleEnvelope());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DbMode_IngestEvents_DisallowedIp_ReturnsForbidden()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            // 白名單只放一個跟測試用的假來源 IP 不一樣的位址——驗證 IP 檢查先於金鑰檢查生效
            builder.UseSetting("AllowedClientIps:0", "10.0.0.1");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("192.168.1.1"))));
        }, allowLocalhost: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");

        var response = await client.PostAsJsonAsync("/api/ingest/events", SampleEnvelope());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DbMode_IngestEvents_CorrectKeyAndAllowedIp_PersistsMessageAndReturnsOk()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");
        var envelope = SampleEnvelope(webhookEventId: "evt-roundtrip");

        var response = await client.PostAsJsonAsync("/api/ingest/events", envelope);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var saved = await dbContext.GroupMessages.SingleAsync(m => m.WebhookEventId == "evt-roundtrip");
        Assert.Equal(envelope.LineMessageId, saved.LineMessageId);
        Assert.Equal(envelope.GroupId, saved.GroupId);
    }

    [Fact]
    public async Task DbMode_IngestEvents_DuplicateWebhookEventId_StillReturnsOk()
    {
        // IIngestSink 的契約：重複也是「成功」——呼叫端（Line 端的 HttpIngestSink）不需要
        // 分辨新寫入還是重複，只要 2xx 都代表可以把 outbox 項目刪掉
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");
        var envelope = SampleEnvelope(webhookEventId: "evt-dup");

        var first = await client.PostAsJsonAsync("/api/ingest/events", envelope);
        var second = await client.PostAsJsonAsync("/api/ingest/events", envelope);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        Assert.Equal(1, await dbContext.GroupMessages.CountAsync(m => m.WebhookEventId == "evt-dup"));
    }

    private static IngestEnvelope SampleEnvelope(string webhookEventId = "evt-1") => new(
        WebhookEventId: webhookEventId,
        LineMessageId: "m1",
        GroupId: "G1",
        UserId: "U1",
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.UtcNow,
        ReceivedAt: DateTimeOffset.UtcNow,
        HasContent: false,
        ContentFileName: null);
}
