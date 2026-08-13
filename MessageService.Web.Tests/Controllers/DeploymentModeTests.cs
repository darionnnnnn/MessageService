using System.Net;
using System.Net.Http.Json;
using System.Text;
using MessageService.Data;
using MessageService.Models;
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
            // 不用預設的 Development 環境：ASP.NET Core 只在 Development 才自動載入 user-secrets，
            // 這台開發機的 user-secrets 存了一把真的 LINE Channel Access Token（供先前手動對真實
            // LINE bot 測試用），會在這裡把 Line:ChannelAccessToken 意外填成非空值，掩蓋掉
            // OutboundHere 驗證規則在「真的沒有設定」時應有的行為——在 CI／乾淨環境是測不出這個
            // 差異的，只有這台機器會因為殘留密鑰而silently通過。比照 MessageService.Web.Tests
            // 的 WebAppFactoryFixture 用同一招：換一個沒有對應 appsettings.*.json、user-secrets
            // 也不會觸發的環境名稱，讓 appsettings.json（值都是空字串／類別預設）成為唯一基底
            builder.UseEnvironment("Testing");
            // 這份檔案的測試關注的是路由閘門／認證／啟動驗證，不是媒體下載——OutboundHere
            // 的驗證規則本身（true 卻沒有 ChannelAccessToken 要擋啟動）已經在
            // DeploymentValidatorTests 單元測試完整覆蓋，這裡關掉它當預設值，
            // 讓不相關的測試不必每個都額外設定；真的要測 OutboundHere 交互作用時
            // 可以在 configure 裡再蓋掉這個設定（同一個 key 後設定的值會贏）
            builder.UseSetting("Line:OutboundHere", "false");
            // appsettings.Development.json 原本把這個覆寫成 Sqlite；換到 Testing 環境後
            // appsettings.json 的預設值（SqlServer，且 ConnectionStrings:SqlServer 是空字串）
            // 會生效——這份檔案的每個測試都用 Sqlite 暫存檔，沒有任何一個真的要測 SqlServer
            builder.UseSetting("Database:Provider", "Sqlite");
            configure(builder);
            if (allowLocalhost)
            {
                builder.UseSetting("Ingest:AllowedClientIps:0", "127.0.0.1");
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

        // 合併後：Db 模式仍掛著檢視端的 MapStaticAssets／.WithStaticAssets() 靜態資源後援
        // （{**path:file}，只接受 GET/HEAD），對任何未註冊路徑的非 GET/HEAD 請求，路由層會先判定
        // 「路徑有東西 match、但方法不對」而回 405，不會再往下探到「完全沒有 match」的 404。
        // 405 跟 404 對這裡真正要驗證的事（webhook controller 沒被路由到、簽章驗證邏輯完全沒被
        // 執行）是等價的——都不是 401，代表請求沒有走到 LineWebhookController 裡面
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"預期 404 或 405（皆代表 webhook controller 未被路由到），實際是 {response.StatusCode}");
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
    public async Task LineMode_ViewerRoutes_DoNotExist()
    {
        // 合併後最重要的安全邊界：Line／Edge 主機沒有本機資料庫，檢視端的頁面與 API
        // 必須整組不存在（404），不能因為現在跟檢視端同一個行程就意外暴露出來
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Ingest:BaseUrl", "https://db-host.example");
            builder.UseSetting("Ingest:ApiKey", "test-key");
        }, allowLocalhost: false);
        using var client = factory.CreateClient();

        var homeResponse = await client.GetAsync("/");
        var groupsResponse = await client.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.NotFound, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, groupsResponse.StatusCode);
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("Db")]
    public async Task FullOrDbMode_ViewerRoutes_Work(string mode)
    {
        // Full／Db 模式（有本機資料庫）檢視端要能正常運作；同時驗證檢視端白名單
        // （Viewer:AllowedClientIps）跟 ingest 白名單是分開的兩個設定
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", mode);
            if (mode == "Full")
            {
                builder.UseSetting("Line:ChannelSecret", "secret");
            }
            else
            {
                builder.UseSetting("Ingest:ApiKey", "correct-key");
            }
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxPath}");
            builder.UseSetting("Viewer:AllowedClientIps:0", "127.0.0.1");
        });
        using var client = factory.CreateClient();

        var homeResponse = await client.GetAsync("/");
        var groupsResponse = await client.GetAsync("/api/groups");

        // 首頁走 Razor View（真的算圖顯示成不成功不是這裡的重點），只確認路由沒被
        // DeploymentModeConvention 移除即可；純 API 的 Groups 端點才嚴格驗證 200
        Assert.NotEqual(HttpStatusCode.NotFound, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, groupsResponse.StatusCode);
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

    [Theory]
    [InlineData("ingest")]
    [InlineData("ingest-content")]
    public void LineModeWithOutboundHere_NamedHttpClients_CarryApiKeyHeader(string clientName)
    {
        // 真的抓過一次的 bug：ApiContentWorkSource／ApiProfileStore 打的請求完全沒帶
        // X-Ingest-Key，全部被 IngestApiKeyMiddleware 擋成 401——起兩個真實行程互打才測出來，
        // 因為其餘測試都是直接呼叫 controller 或用 FakeHttpMessageHandler，沒有一個真的
        // 經過這裡驗證的 HttpClient 具名註冊本身。修法是在註冊時就把標頭設成預設值，
        // 這裡直接從 DI 解析具名 client 確認標頭真的在，防止回歸。
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:ChannelAccessToken", "dummy-token");
            builder.UseSetting("Ingest:BaseUrl", "https://db-host.example");
            builder.UseSetting("Ingest:ApiKey", "the-shared-secret");
        }, allowLocalhost: false);

        using var scope = factory.Services.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient(clientName);

        var headerValues = client.DefaultRequestHeaders.GetValues("X-Ingest-Key");
        Assert.Equal("the-shared-secret", Assert.Single(headerValues));
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
            builder.UseSetting("Ingest:AllowedClientIps:0", "10.0.0.1");
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

    // === Stage 3：content-work／profiles 端點的真實 HTTP 整合測試 ===
    // UploadContent 直接操作 Request.Body／HttpContext.Features，沒辦法乾淨地用直接建構
    // controller 的方式單元測試（IngestControllerTests 已經測了其餘端點），這裡用真實
    // host + 真實 HTTP 往返涵蓋。

    private WebApplicationFactory<Program> CreateDbModeFactoryWithContentWork(string dbPath) =>
        CreateFactory(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "correct-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={dbPath}");
        });

    [Fact]
    public async Task ContentWorkLifecycle_SubmitUploadThenNoLongerPending()
    {
        using var factory = CreateDbModeFactoryWithContentWork(_dbPath);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");

        // 先送一則帶媒體的事件，取得 ingest API 配的 ContentId（跟真實 Line 端的流程一致）
        var envelope = SampleEnvelope(webhookEventId: "evt-content-lifecycle") with { MessageType = "image", Text = null, HasContent = true };
        var submitResponse = await client.PostAsJsonAsync("/api/ingest/events", envelope);
        var submitBody = await submitResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        var contentId = submitBody!.ContentId!.Value;

        // 出現在待辦清單
        var pendingIds = await client.GetFromJsonAsync<List<long>>("/api/ingest/content-work");
        Assert.Contains(contentId, pendingIds!);

        // 單筆詳情
        var itemResponse = await client.GetAsync($"/api/ingest/content-work/{contentId}");
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        var item = await itemResponse.Content.ReadFromJsonAsync<ContentWorkItem>();
        Assert.Equal(envelope.LineMessageId, item!.LineMessageId);
        Assert.Equal("image", item.MessageType);

        // 上傳 blob——raw body，Content-Type 標頭即內容型別
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03 };
        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        var uploadResponse = await client.PutAsync($"/api/ingest/content/{contentId}", uploadContent);
        Assert.Equal(HttpStatusCode.NoContent, uploadResponse.StatusCode);

        // 上傳後不再是 Pending：GET 單筆詳情要 404，且從待辦清單消失
        var afterUploadItem = await client.GetAsync($"/api/ingest/content-work/{contentId}");
        Assert.Equal(HttpStatusCode.NotFound, afterUploadItem.StatusCode);
        var pendingAfter = await client.GetFromJsonAsync<List<long>>("/api/ingest/content-work");
        Assert.DoesNotContain(contentId, pendingAfter!);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var saved = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Completed, saved.DownloadStatus);
        Assert.Equal(bytes, saved.Content);
        Assert.Equal("image/jpeg", saved.ContentType);
    }

    [Fact]
    public async Task ContentWork_MarkFailed_SetsFailedStatus()
    {
        using var factory = CreateDbModeFactoryWithContentWork(_dbPath);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");

        var envelope = SampleEnvelope(webhookEventId: "evt-content-fail") with { MessageType = "video", Text = null, HasContent = true };
        var submitResponse = await client.PostAsJsonAsync("/api/ingest/events", envelope);
        var contentId = (await submitResponse.Content.ReadFromJsonAsync<IngestEventResponse>())!.ContentId!.Value;

        var failResponse = await client.PostAsync($"/api/ingest/content/{contentId}/failed", null);

        Assert.Equal(HttpStatusCode.NoContent, failResponse.StatusCode);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var saved = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Failed, saved.DownloadStatus);
    }

    [Fact]
    public async Task ProfileStalenessAndUpsert_RoundTripsOverRealHttp()
    {
        using var factory = CreateDbModeFactoryWithContentWork(_dbPath);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "correct-key");

        // 從沒有任何快取資料開始，理應判定為過期（需要刷新）
        var stalenessResponse = await client.GetAsync(
            $"/api/ingest/profiles/staleness?groupId=Gprof&userId=Uprof&cutoff={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}");
        var staleness = await stalenessResponse.Content.ReadFromJsonAsync<ProfileStaleness>();
        Assert.True(staleness!.GroupStale);
        Assert.True(staleness.MemberStale);

        // upsert 群組與成員
        var groupResponse = await client.PostAsJsonAsync("/api/ingest/profiles/group",
            new GroupSummary("Gprof", "測試群組", "https://example/g.png"));
        Assert.Equal(HttpStatusCode.NoContent, groupResponse.StatusCode);

        var memberResponse = await client.PostAsJsonAsync("/api/ingest/profiles/member",
            new MemberUpsertRequest("Gprof", new MemberProfile("Uprof", "測試成員", "https://example/u.png")));
        Assert.Equal(HttpStatusCode.NoContent, memberResponse.StatusCode);

        // 剛更新過，用「很久以前」當 cutoff 應該判定為新鮮（不需要刷新）
        var freshResponse = await client.GetAsync(
            $"/api/ingest/profiles/staleness?groupId=Gprof&userId=Uprof&cutoff={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-7).ToString("O"))}");
        var freshStaleness = await freshResponse.Content.ReadFromJsonAsync<ProfileStaleness>();
        Assert.False(freshStaleness!.GroupStale);
        Assert.False(freshStaleness.MemberStale);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var group = await dbContext.Groups.SingleAsync(g => g.GroupId == "Gprof");
        Assert.Equal("測試群組", group.GroupName);
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
