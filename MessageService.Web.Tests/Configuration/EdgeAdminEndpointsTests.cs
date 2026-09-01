using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageService.Outbox;
using MessageService.Tests.TestSupport;
using MessageService.Web.Configuration;
using MessageService.Web.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void CorruptedFile_HostBootsNormally_EffectiveSettingsFallbackToAppSettings()
    {
        // 寫入毀損檔案
        var dbDir = Path.Combine(_tempDir, "Db");
        Directory.CreateDirectory(dbDir);
        var settingsPath = Path.Combine(dbDir, "edge-settings.dat");
        File.WriteAllBytes(settingsPath, [0xAA, 0xBB, 0xCC, 0xDD]);

        using var factory = CreateEdgeFactory();
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();

        // 站台能正常啟動，LoadStatus 為 Unreadable
        Assert.Equal(EncryptedSettingsLoadStatus.Unreadable, store.LoadStatus);

        // 設定值退回 appsettings
        Assert.Equal("initial-secret", config["Line:ChannelSecret"]);
        Assert.Equal("initial-key", config["Ingest:ApiKey"]);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WithCorruptedFile_ContainsAlertBanner()
    {
        var dbDir = Path.Combine(_tempDir, "Db");
        Directory.CreateDirectory(dbDir);
        var settingsPath = Path.Combine(dbDir, "edge-settings.dat");
        File.WriteAllBytes(settingsPath, [0xAA, 0xBB, 0xCC, 0xDD]);

        using var factory = CreateEdgeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div class=\"alert-danger\">", html);
        Assert.Contains("加密設定檔存在但無法解密", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WithoutSettingsFile_DoesNotContainAlertBanner()
    {
        using var factory = CreateEdgeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<div class=\"alert-danger\">", html);
        Assert.DoesNotContain("加密設定檔存在但無法解密", html);
    }

    [Fact]
    public async Task EdgeAdmin_Post_WhenInitiallyUnreadable_SucceedsAndRecoversToLoaded()
    {
        // 初始為毀損狀態
        var dbDir = Path.Combine(_tempDir, "Db");
        Directory.CreateDirectory(dbDir);
        var settingsPath = Path.Combine(dbDir, "edge-settings.dat");
        File.WriteAllBytes(settingsPath, [0xAA, 0xBB, 0xCC, 0xDD]);

        using var factory = CreateEdgeFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        Assert.Equal(EncryptedSettingsLoadStatus.Unreadable, store.LoadStatus);

        // 重新填寫並送出表單
        var form = new Dictionary<string, string>
        {
            ["lineChannelSecret"] = "repaired-secret",
            ["webhookMode"] = "Any",
        };
        using var content = new FormUrlEncodedContent(form);
        var postRes = await client.PostAsync("/edge-admin", content);
        Assert.True(postRes.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        // 復原後狀態為 Loaded，設定值生效
        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, store.LoadStatus);
        var config = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("repaired-secret", config["Line:ChannelSecret"]);
    }

    [Fact]
    public async Task EdgeAdmin_Get_ContainsThreeTabsAndContainersAndSectionTitles()
    {
        using var factory = CreateEdgeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // 三個分頁標題與容器
        Assert.Contains("id=\"tab-label-settings\"", html);
        Assert.Contains("id=\"tab-label-connection\"", html);
        Assert.Contains("id=\"tab-label-troubleshooting\"", html);
        Assert.Contains("設定", html);
        Assert.Contains("連線測試", html);
        Assert.Contains("錯誤排查", html);

        Assert.Contains("id=\"tab-settings\"", html);
        Assert.Contains("id=\"tab-connection\"", html);
        Assert.Contains("id=\"tab-troubleshooting\"", html);

        // 錯誤排查三區塊標題
        Assert.Contains("本機最近錯誤", html);
        Assert.Contains("今日 log 檔尾", html);
        Assert.Contains("EdgeProxy 端錯誤", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenLocalBufferHasEntries_DisplaysEntriesAndEscapesHtml()
    {
        using var factory = CreateEdgeFactory();

        // 寫入帶有 HTML 特殊字元的 Warning
        var ringBuffer = factory.Services.GetRequiredService<LogRingBuffer>();
        ringBuffer.Add(new LogBufferEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            Level: LogLevel.Warning,
            Category: "TestCategory",
            Message: "Local buffer with <script>alert('msg')</script>",
            ExceptionSummary: "InvalidOp: <script>fail</script>"));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(&#39;msg&#39;)&lt;/script&gt;", html);
        Assert.Contains("TestCategory", html);
        Assert.Contains("InvalidOp: &lt;script&gt;fail&lt;/script&gt;", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenLocalBufferEmpty_DisplaysEmptyBufferMessage()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(new LogRingBuffer());
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("目前沒有記錄到警告以上訊息", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenNotUsingEdgeProxy_DisplaysNotUsingEdgeProxy()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "Direct");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("本主機未使用 EdgeProxy", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenEdgeProxyConfigured_FetchFails_DisplaysFailureMessageAndReturns200()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "EdgeProxy");
            builder.UseSetting("Line:OutboundProxyBaseUrl", "http://192.0.2.10/MSLine");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("edge-proxy-errors")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            ReasonPhrase = "Proxy Unavailable"
                        }));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("無法連上 EdgeProxy", html);
        Assert.Contains("請直接查看該主機的 logs 目錄", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenEdgeProxyConfigured_FetchThrowsException_DisplaysFailureMessageAndReturns200()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "EdgeProxy");
            builder.UseSetting("Line:OutboundProxyBaseUrl", "http://192.0.2.10/MSLine");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("edge-proxy-errors")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        throw new HttpRequestException("Connection refused by proxy")));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("無法連上 EdgeProxy", html);
        Assert.Contains("Connection refused by proxy", html);
        Assert.Contains("請直接查看該主機的 logs 目錄", html);
    }

    [Fact]
    public async Task EdgeAdmin_Get_WhenEdgeProxyConfigured_FetchSucceeds_DisplaysProxyEntries()
    {
        var proxyResponse = new ProxyAdminErrorsResponse(
            MachineName: "ProxyServer01",
            ProcessStartTimeUtc: DateTimeOffset.UtcNow.AddHours(-2),
            Entries:
            [
                new LogBufferEntry(
                    TimestampUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
                    Level: LogLevel.Error,
                    Category: "EdgeProxy.Forwarder",
                    Message: "Proxy forwarded error test",
                    ExceptionSummary: "TimeoutException: downstream timed out")
            ]);

        var json = System.Text.Json.JsonSerializer.Serialize(proxyResponse);

        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "EdgeProxy");
            builder.UseSetting("Line:OutboundProxyBaseUrl", "http://192.0.2.10/MSLine");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("edge-proxy-errors")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        }));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("EdgeProxy.Forwarder", html);
        Assert.Contains("Proxy forwarded error test", html);
        Assert.Contains("TimeoutException: downstream timed out", html);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_200Success_ReturnsOkWithDisplayNameAndConnectionTabActive()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:OutboundVia", "Direct");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"displayName\":\"我的機器人\"}", Encoding.UTF8, "application/json")
                        }));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // 驗證連線成功與 bot 名稱
        Assert.Contains("alert-success", html);
        Assert.Contains("連線成功：我的機器人", html);
        Assert.Contains("（經由 Direct）", html);

        // 驗證連線測試分頁為 checked
        Assert.Contains("id=\"tab-nav-connection\" name=\"admin-tab\" class=\"tab-radio\" checked", html);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_401Unauthorized_ReturnsOkWithFailureMessageAnd401()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:OutboundVia", "Direct");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            ReasonPhrase = "Unauthorized",
                            Content = new StringContent("{\"message\":\"Invalid OAuth access token\"}", Encoding.UTF8, "application/json")
                        }));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // 驗證連線失敗與 401
        Assert.Contains("alert-danger", html);
        Assert.Contains("連線失敗：", html);
        Assert.Contains("401", html);
        Assert.Contains("Invalid OAuth access token", html);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_WhenHandlerThrowsHttpRequestException_Returns200WithFailureMessage()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:OutboundVia", "Direct");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        throw new HttpRequestException("Network connection failed to LINE API")));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        // 回應碼為 200（不是 500）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // 驗證連線失敗與例外訊息
        Assert.Contains("alert-danger", html);
        Assert.Contains("連線失敗：", html);
        Assert.Contains("HttpRequestException", html);
        Assert.Contains("Network connection failed to LINE API", html);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_WithOverrideToken_SendsOverrideTokenInAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;

        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:ChannelAccessToken", "configured-token-xyz");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                    {
                        capturedRequest = req;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"displayName\":\"Override Bot\"}", Encoding.UTF8, "application/json")
                        };
                    }));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = "my-override-token-1234"
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-override-token-1234", capturedRequest.Headers.Authorization?.Parameter);

        // 覆寫 token 絕不回顯在頁面上
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("my-override-token-1234", html);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_WithoutOverrideToken_SendsConfiguredTokenInAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;

        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:ChannelAccessToken", "initial-configured-token");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                    {
                        capturedRequest = req;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"displayName\":\"Configured Bot\"}", Encoding.UTF8, "application/json")
                        };
                    }));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("initial-configured-token", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_WhenBotDisplayNameContainsScript_EscapesHtml()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "true");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(MessageService.Services.LineProfileClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(req =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"displayName\":\"<script>alert('xss')</script>\"}", Encoding.UTF8, "application/json")
                        }));
            });
        });

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // 必須逸出
        Assert.DoesNotContain("<script>alert('xss')</script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task EdgeAdmin_WhenOutboundHereFalse_ConnectionTabShowsDisabledMessage_AndPostTestLineDoesNot500()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("Line:OutboundHere", "false");
        });

        using var client = factory.CreateClient();

        // GET 頁面時顯示未啟用訊息且不顯示按鈕
        var getResponse = await client.GetAsync("/edge-admin");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getHtml = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains("此主機未啟用 LINE outbound，無法測試", getHtml);
        Assert.DoesNotContain("測試目前生效的 Token", getHtml);
        Assert.DoesNotContain("用這個 Token 測試（不儲存）", getHtml);

        // POST /edge-admin/test-line 時不 500，安全回傳 200 與同一句話
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = "some-token"
        };
        var postResponse = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        var postHtml = await postResponse.Content.ReadAsStringAsync();
        Assert.Contains("此主機未啟用 LINE outbound，無法測試", postHtml);
        Assert.DoesNotContain("測試目前生效的 Token", postHtml);
    }

    [Fact]
    public async Task EdgeAdmin_TestLine_DisallowedClientIp_Returns403()
    {
        using var factory = CreateEdgeFactory(builder =>
        {
            builder.UseSetting("EdgeAdmin:AllowedClientIps:0", "192.0.2.1");
        }, clientIp: "127.0.0.1");

        using var client = factory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["overrideToken"] = ""
        };

        var response = await client.PostAsync("/edge-admin/test-line", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void ReadLogTail_WhenFileMissing_ReturnsNoLogMessage()
    {
        var missing = Path.Combine(_tempDir, "logs", "messageservice-1999-01-01.log");

        var (content, error) = EdgeAdminEndpoints.ReadLogTail(missing);

        Assert.Null(content);
        Assert.Equal("今天尚無 log 檔", error);
    }

    [Fact]
    public void ReadLogTail_WhenFileExists_ReturnsContent()
    {
        var path = Path.Combine(_tempDir, "sample.log");
        File.WriteAllText(
            path,
            "2026-09-01 10:00:00|INFO|App|Service started <script>" + Environment.NewLine
                + "2026-09-01 10:01:00|WARN|App|Disk check" + Environment.NewLine,
            Encoding.UTF8);

        var (content, error) = EdgeAdminEndpoints.ReadLogTail(path);

        Assert.Null(error);
        Assert.Contains("Service started <script>", content);
        Assert.Contains("Disk check", content);
    }

    [Fact]
    public void ReadLogTail_WhenMoreThan100Lines_KeepsLast100()
    {
        var path = Path.Combine(_tempDir, "long.log");
        var sb = new StringBuilder();
        for (var i = 1; i <= 150; i++)
        {
            sb.AppendLine($"Log line #{i:D3}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

        var (content, error) = EdgeAdminEndpoints.ReadLogTail(path);

        Assert.Null(error);
        Assert.Contains("Log line #150", content);
        Assert.Contains("Log line #051", content);
        Assert.DoesNotContain("Log line #050", content);
    }

    [Fact]
    public void ReadLogTail_WhenFileCannotBeOpened_ReturnsErrorInsteadOfThrowing()
    {
        // 檔案存在但開不起來（別的行程獨佔）時要顯示原因，不能讓整頁炸掉
        var path = Path.Combine(_tempDir, "locked.log");
        File.WriteAllText(path, "line", Encoding.UTF8);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var (content, error) = EdgeAdminEndpoints.ReadLogTail(path);

        Assert.Null(content);
        Assert.StartsWith("無法讀取 log 檔：", error);
    }

    [Fact]
    public async Task EdgeAdmin_Get_RendersTodayLogSection()
    {
        using var factory = CreateEdgeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/edge-admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("今日 log 檔尾", html);
    }
}
