using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageService.Outbox;
using MessageService.Tests.TestSupport;
using MessageService.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

public class EdgeAdminEndpointsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"messageservice-edgeadmin-test-{Guid.NewGuid():N}");
    private readonly string _outboxDbPath;
    private readonly PlaintextSettingsProtector _protector = new();

    public EdgeAdminEndpointsTests()
    {
        Directory.CreateDirectory(_tempDir);
        _outboxDbPath = Path.Combine(_tempDir, "outbox.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private WebApplicationFactory<Program> CreateEdgeFactory(
        Action<IWebHostBuilder>? configure = null,
        string clientIp = "127.0.0.1")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "initial-secret");
            builder.UseSetting("Line:ChannelAccessToken", "initial-token");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "initial-key");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.UseSetting("EdgeAdmin:AllowedClientIps:0", "127.0.0.1");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(_protector);
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse(clientIp)));
            });

            configure?.Invoke(builder);
        });
    }

    private static string ComputeSignature(string secret, byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public async Task EdgeAdmin_DisallowedClientIp_Returns403()
    {
        // EdgeAdmin:AllowedClientIps 設成不含測試來源的網段 (192.0.2.1) -> GET /edge-admin 回 403
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("EdgeAdmin:AllowedClientIps:0", "192.0.2.1");
        }, clientIp: "127.0.0.1");

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EdgeAdmin_EmptyAllowlist_Returns403()
    {
        // 空的 EdgeAdmin:AllowedClientIps -> 403（全擋）
        using var factory = CreateEdgeFactory(builder =>
        {
            // 透過清空覆蓋
            builder.UseSetting("EdgeAdmin:AllowedClientIps:0", "");
        }, clientIp: "127.0.0.1");

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EdgeAdmin_AllowedIp_Returns200WithHtmlContentType()
    {
        // 白名單允許時 -> GET /edge-admin 回 200、Content-Type 是 text/html
        using var factory = CreateEdgeFactory(clientIp: "127.0.0.1");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Edge 管理設定", html);
    }

    [Theory]
    [InlineData("Core")]
    [InlineData("EdgeProxy")]
    public async Task NonEdgeModes_EdgeAdminEndpoint_Returns404(string mode)
    {
        // 非 Edge 模式（例如 Core 或 EdgeProxy）-> /edge-admin 回 404
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", mode);
            if (mode == "Core")
            {
                builder.UseSetting("Ingest:ApiKey", "the-key");
                builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_tempDir, "core.db")}");
                builder.UseSetting("Viewer:AllowedClientIps:0", "127.0.0.1");
            }
            else if (mode == "EdgeProxy")
            {
                builder.UseSetting("EdgeProxy:TargetBaseUrl", "https://edge.example");
            }
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EdgeAdmin_PostForm_SavesNewValues_AndBlankSecretsRetainOriginal()
    {
        // POST 表單後：EdgeSettingsStore.Read() 讀得到新值；機密欄位留空時原值不變
        using var factory = CreateEdgeFactory(clientIp: "127.0.0.1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false // 驗證 302/303 PRG
        });

        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        // 先建立初始設定值在加密檔
        store.Save(new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "original-secret-1234",
            ["Line:ChannelAccessToken"] = "original-token-5678",
            ["Ingest:ApiKey"] = "original-key-9999"
        });

        // 提交表單：LineChannelSecret 改為 new-secret，其餘兩個機密留空（留空＝維持原值）
        var form = new Dictionary<string, string>
        {
            ["lineChannelSecret"] = "new-secret-5555",
            ["lineChannelAccessToken"] = "",
            ["ingestApiKey"] = "",
            ["ingestAllowedClientIps"] = "192.0.2.1\n192.0.2.2",
            ["webhookSourceMode"] = "AllowlistOnly",
            ["webhookSourceAllowedIps"] = "192.0.2.100"
        };

        var postResponse = await client.PostAsync("/edge-admin", new FormUrlEncodedContent(form));

        // 導回 GET /edge-admin (302/303)
        Assert.True(
            postResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
            $"預期 Redirect/SeeOther，實際是 {postResponse.StatusCode}");
        Assert.Equal("/edge-admin?saved=true", postResponse.Headers.Location?.ToString());

        // 驗證 EdgeSettingsStore.Read() 內容
        var readValues = store.Read();
        Assert.Equal("new-secret-5555", readValues["Line:ChannelSecret"]);
        Assert.Equal("original-token-5678", readValues["Line:ChannelAccessToken"]); // 原值不變
        Assert.Equal("original-key-9999", readValues["Ingest:ApiKey"]); // 原值不變
        Assert.Equal("192.0.2.1", readValues["Ingest:AllowedClientIps:0"]);
        Assert.Equal("192.0.2.2", readValues["Ingest:AllowedClientIps:1"]);
        Assert.Equal("AllowlistOnly", readValues["WebhookSource:Mode"]);
        Assert.Equal("192.0.2.100", readValues["WebhookSource:AllowedIps:0"]);
    }

    [Fact]
    public async Task EdgeAdmin_PostArrayFrom3To2_NoStaleThirdIndex()
    {
        // POST 陣列從 3 筆改成 2 筆 -> 加密檔裡沒有殘留第 3 個索引鍵（這條一定要有）
        using var factory = CreateEdgeFactory(clientIp: "127.0.0.1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        // 先有 3 筆
        store.Save(new Dictionary<string, string?>
        {
            ["Ingest:AllowedClientIps:0"] = "192.0.2.1",
            ["Ingest:AllowedClientIps:1"] = "192.0.2.2",
            ["Ingest:AllowedClientIps:2"] = "192.0.2.3",
            ["WebhookSource:AllowedIps:0"] = "192.0.2.10",
            ["WebhookSource:AllowedIps:1"] = "192.0.2.20",
            ["WebhookSource:AllowedIps:2"] = "192.0.2.30"
        });

        // 透過 POST 改成 2 筆
        var form = new Dictionary<string, string>
        {
            ["lineChannelSecret"] = "",
            ["lineChannelAccessToken"] = "",
            ["ingestApiKey"] = "",
            ["ingestAllowedClientIps"] = "192.0.2.101\n192.0.2.102",
            ["webhookSourceMode"] = "AllowlistOnly",
            ["webhookSourceAllowedIps"] = "192.0.2.201\n192.0.2.202"
        };

        var postResponse = await client.PostAsync("/edge-admin", new FormUrlEncodedContent(form));
        Assert.True(postResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther);

        var readValues = store.Read();

        // 驗證新值
        Assert.Equal("192.0.2.101", readValues["Ingest:AllowedClientIps:0"]);
        Assert.Equal("192.0.2.102", readValues["Ingest:AllowedClientIps:1"]);
        Assert.Equal("192.0.2.201", readValues["WebhookSource:AllowedIps:0"]);
        Assert.Equal("192.0.2.202", readValues["WebhookSource:AllowedIps:1"]);

        // 第 3 個索引不得再有值。加密來源是疊在 appsettings 之上、逐鍵合併的，所以「移除」
        // 不能只是從加密檔字典刪掉——那樣 appsettings 的同名索引會浮上來繼續生效。
        // 正確做法是用空字串哨兵蓋掉（IpNetworkParser 會略過空白），所以這裡驗的是
        // 「沒有非空值」而不是「鍵不存在」。真正生效的合併結果另有
        // EdgeAdmin_PostShrinksArray_EffectiveConfigurationAlsoShrinks 把關
        Assert.True(
            !readValues.TryGetValue("Ingest:AllowedClientIps:2", out var staleIngest) || string.IsNullOrEmpty(staleIngest),
            "Ingest:AllowedClientIps:2 不應再有值");
        Assert.True(
            !readValues.TryGetValue("WebhookSource:AllowedIps:2", out var staleWebhook) || string.IsNullOrEmpty(staleWebhook),
            "WebhookSource:AllowedIps:2 不應再有值");
    }

    [Fact]
    public async Task EdgeAdmin_HotReload_PostChannelSecret_OldSecretGets401_NewSecretGets200()
    {
        // 端到端熱生效：POST 改掉 Line:ChannelSecret 後，用舊 secret 簽的 webhook 立刻 401、
        // 用新 secret 簽的 200（不重建 host）。
        using var factory = CreateEdgeFactory(clientIp: "127.0.0.1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        // 初始狀態：Line:ChannelSecret 是 initial-secret
        using var reqInitial = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        reqInitial.Headers.Add("X-Line-Signature", ComputeSignature("initial-secret", body));
        var resInitial = await client.SendAsync(reqInitial);
        Assert.Equal(HttpStatusCode.OK, resInitial.StatusCode);

        // 透過 POST /edge-admin 改掉 Line:ChannelSecret
        var form = new Dictionary<string, string>
        {
            ["lineChannelSecret"] = "rotated-channel-secret",
            ["lineChannelAccessToken"] = "",
            ["ingestApiKey"] = "",
            ["ingestAllowedClientIps"] = "",
            ["webhookSourceMode"] = "Any",
            ["webhookSourceAllowedIps"] = ""
        };
        var adminResponse = await client.PostAsync("/edge-admin", new FormUrlEncodedContent(form));
        Assert.True(adminResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther);

        // 用舊 secret 簽的 webhook 立刻 401（不重建 host）
        using var reqOld = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        reqOld.Headers.Add("X-Line-Signature", ComputeSignature("initial-secret", body));
        var resOld = await client.SendAsync(reqOld);
        Assert.Equal(HttpStatusCode.Unauthorized, resOld.StatusCode);

        // 用新 secret 簽的 webhook 200
        using var reqNew = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        reqNew.Headers.Add("X-Line-Signature", ComputeSignature("rotated-channel-secret", body));
        var resNew = await client.SendAsync(reqNew);
        Assert.Equal(HttpStatusCode.OK, resNew.StatusCode);
    }

    [Fact]
    public async Task EdgeAdmin_PostShrinksArray_EffectiveConfigurationAlsoShrinks()
    {
        // 加密來源是疊在 appsettings 之上、逐鍵合併的。只寫新項目的話，appsettings 裡
        // 多出來的索引仍然存在、仍然生效——被移除的那筆其實沒被移除。
        // 這條斷言的是「合併後真正生效的值」，不是加密檔字典本身
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Ingest:AllowedClientIps:0", "192.0.2.1/32");
            builder.UseSetting("Ingest:AllowedClientIps:1", "192.0.2.2/32");
            builder.UseSetting("Ingest:AllowedClientIps:2", "192.0.2.3/32");
        });
        using var client = factory.CreateClient();

        var form = new Dictionary<string, string>
        {
            ["ingestAllowedClientIps"] = "192.0.2.1/32\n192.0.2.2/32",
            ["webhookMode"] = "Any",
            ["webhookAllowedIps"] = "",
        };
        using var content = new FormUrlEncodedContent(form);
        var response = await client.PostAsync("/edge-admin", content);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.SeeOther);

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var effective = configuration.GetSection("Ingest:AllowedClientIps").Get<string[]>() ?? [];
        var nonEmpty = effective.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

        Assert.Equal(["192.0.2.1/32", "192.0.2.2/32"], nonEmpty);
        Assert.DoesNotContain("192.0.2.3/32", nonEmpty);
    }

    [Fact]
    public async Task EdgeAdmin_PostClearsArray_EffectiveConfigurationIsEmpty()
    {
        // 清空整份清單是最嚴重的情況：加密檔完全沒有該前綴的鍵，appsettings 的整份清單原封生效
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Ingest:AllowedClientIps:0", "192.0.2.1/32");
            builder.UseSetting("Ingest:AllowedClientIps:1", "192.0.2.2/32");
        });
        using var client = factory.CreateClient();

        var form = new Dictionary<string, string>
        {
            ["ingestAllowedClientIps"] = "",
            ["webhookMode"] = "Any",
            ["webhookAllowedIps"] = "",
        };
        using var content = new FormUrlEncodedContent(form);
        await client.PostAsync("/edge-admin", content);

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var effective = (configuration.GetSection("Ingest:AllowedClientIps").Get<string[]>() ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();

        Assert.Empty(effective);
    }
}
