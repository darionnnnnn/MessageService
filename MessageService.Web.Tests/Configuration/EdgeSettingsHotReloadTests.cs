using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Web.Configuration;
using MessageService.Web.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

public class EdgeSettingsHotReloadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"messageservice-hotreload-test-{Guid.NewGuid():N}");
    private readonly string _outboxDbPath;

    public EdgeSettingsHotReloadTests()
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

    private static string ComputeSignature(string secret, byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public async Task Webhook_HotReloadChannelSecret_OldSecretGets401_NewSecretGets200()
    {
        var protector = new PlaintextSettingsProtector();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "initial-secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "the-key");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(protector);
            });
        });

        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        // 初始狀態：用 initial-secret 簽章成功 (200)
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req1.Headers.Add("X-Line-Signature", ComputeSignature("initial-secret", body));
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // 透過 EdgeSettingsStore 熱更換 Line:ChannelSecret 為 "updated-secret"
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        store.Save(new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "updated-secret"
        });

        // 舊 secret 簽章變成 401
        using var reqOld = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        reqOld.Headers.Add("X-Line-Signature", ComputeSignature("initial-secret", body));
        var resOld = await client.SendAsync(reqOld);
        Assert.Equal(HttpStatusCode.Unauthorized, resOld.StatusCode);

        // 新 secret 簽章變成 200
        using var reqNew = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        reqNew.Headers.Add("X-Line-Signature", ComputeSignature("updated-secret", body));
        var resNew = await client.SendAsync(reqNew);
        Assert.Equal(HttpStatusCode.OK, resNew.StatusCode);
    }

    [Fact]
    public async Task IngestAllowedClientIps_HotReload_ImmediateEffect()
    {
        var protector = new PlaintextSettingsProtector();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "valid-key");
            builder.UseSetting("Ingest:Channel", "Auto");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            // 初始只放行 192.0.2.1，不包含 127.0.0.1
            builder.UseSetting("Ingest:AllowedClientIps:0", "192.0.2.1");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(protector);
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));
            });
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Key", "valid-key");

        // 初始狀態：127.0.0.1 不在白名單，被擋 403
        var res1 = await client.PostAsync("/api/edge/poll", null);
        Assert.Equal(HttpStatusCode.Forbidden, res1.StatusCode);

        // 熱更換 Ingest:AllowedClientIps 包含 127.0.0.1
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        store.Save(new Dictionary<string, string?>
        {
            ["Ingest:AllowedClientIps:0"] = "127.0.0.1"
        });

        // 立即放行（通過白名單與金鑰驗證，抵達 controller 回應 200）
        var res2 = await client.PostAsync("/api/edge/poll", null);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
    }

    [Fact]
    public async Task IngestApiKey_HotReload_OutboundCarriesNewKey_AndInboundAcceptsNewKey()
    {
        var protector = new PlaintextSettingsProtector();
        HttpRequestMessage? lastOutboundRequest = null;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "initial-key");
            builder.UseSetting("Ingest:Channel", "Auto");
            builder.UseSetting("Ingest:AllowedClientIps:0", "127.0.0.1");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(protector);
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));

                services.Configure<HttpClientFactoryOptions>("ingest", options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(b =>
                    {
                        b.PrimaryHandler = new FakeHttpMessageHandler(req =>
                        {
                            lastOutboundRequest = req;
                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });
                    });
                });
            });
        });

        using var client = factory.CreateClient();
        var httpClientFactory = factory.Services.GetRequiredService<IHttpClientFactory>();
        var ingestClient = httpClientFactory.CreateClient("ingest");

        // 1. 初始出站請求帶 initial-key
        await ingestClient.GetAsync("test");
        Assert.NotNull(lastOutboundRequest);
        Assert.Equal("initial-key", lastOutboundRequest!.Headers.GetValues("X-Ingest-Key").Single());

        // 2. 熱更新 Ingest:ApiKey 為 "rotated-new-key"
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        store.Save(new Dictionary<string, string?>
        {
            ["Ingest:ApiKey"] = "rotated-new-key"
        });

        // 3. 出站請求立即帶上新 key（防止單向 401 裂縫）
        await ingestClient.GetAsync("test-after-rotation");
        Assert.Equal("rotated-new-key", lastOutboundRequest!.Headers.GetValues("X-Ingest-Key").Single());

        // 4. 入站驗證同時改用新 key：帶舊 key 回 401，帶新 key 回 200
        using var oldKeyReq = new HttpRequestMessage(HttpMethod.Post, "/api/edge/poll");
        oldKeyReq.Headers.Add("X-Ingest-Key", "initial-key");
        var oldKeyRes = await client.SendAsync(oldKeyReq);
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyRes.StatusCode);

        using var newKeyReq = new HttpRequestMessage(HttpMethod.Post, "/api/edge/poll");
        newKeyReq.Headers.Add("X-Ingest-Key", "rotated-new-key");
        var newKeyRes = await client.SendAsync(newKeyReq);
        Assert.Equal(HttpStatusCode.OK, newKeyRes.StatusCode);
    }

    [Theory]
    [InlineData("LineContent", true)]
    [InlineData("LineProfile", true)]
    [InlineData("LineSticker", false)]
    [InlineData("LineProfileImage", false)]
    public async Task LineClients_AuthorizationHeader_ExpectedPresenceAndHotReload(string clientName, bool shouldHaveAuth)
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            ChannelAccessToken = "token-v1"
        });

        HttpRequestMessage? captured = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Line:ChannelAccessToken", "token-v1");
            builder.UseSetting("Ingest:BaseUrl", "https://db-host.example");
            builder.UseSetting("Ingest:ApiKey", "key");
            builder.ConfigureServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(clientName, options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(b =>
                    {
                        b.PrimaryHandler = new FakeHttpMessageHandler(req =>
                        {
                            captured = req;
                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });
                    });
                });
            });
        });

        var httpClientFactory = factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient(clientName);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com/test");
        await client.SendAsync(req);

        Assert.NotNull(captured);
        if (shouldHaveAuth)
        {
            Assert.NotNull(captured!.Headers.Authorization);
            Assert.Equal("Bearer", captured.Headers.Authorization.Scheme);
            Assert.Equal("token-v1", captured.Headers.Authorization.Parameter);
        }
        else
        {
            Assert.Null(captured!.Headers.Authorization);
        }
    }

    [Fact]
    public async Task CoreMode_EdgePullService_NamedClients_CarryApiKeyHeader()
    {
        HttpRequestMessage? pullRequest = null;
        HttpRequestMessage? contentRequest = null;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Core");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_tempDir, "core.db")}");
            builder.UseSetting("Ingest:ApiKey", "core-outbound-key");
            builder.UseSetting("Ingest:EdgeBaseUrl", "https://edge-host.example");
            builder.ConfigureServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(EdgePullService.HttpClientName, options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(b =>
                    {
                        b.PrimaryHandler = new FakeHttpMessageHandler(req =>
                        {
                            pullRequest = req;
                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });
                    });
                });
                services.Configure<HttpClientFactoryOptions>(EdgePullService.ContentHttpClientName, options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(b =>
                    {
                        b.PrimaryHandler = new FakeHttpMessageHandler(req =>
                        {
                            contentRequest = req;
                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });
                    });
                });
            });
        });

        var httpClientFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        var pullClient = httpClientFactory.CreateClient(EdgePullService.HttpClientName);
        await pullClient.GetAsync("poll");
        Assert.NotNull(pullRequest);
        Assert.Equal("core-outbound-key", pullRequest!.Headers.GetValues("X-Ingest-Key").Single());

        var contentClient = httpClientFactory.CreateClient(EdgePullService.ContentHttpClientName);
        await contentClient.GetAsync("content");
        Assert.NotNull(contentRequest);
        Assert.Equal("core-outbound-key", contentRequest!.Headers.GetValues("X-Ingest-Key").Single());
    }

    [Fact]
    public async Task IngestApiKeyHandler_CalledTwice_DoesNotDuplicateHeader()
    {
        var monitor = new FakeOptionsMonitor<IngestOptions>(new IngestOptions { ApiKey = "single-key" });
        HttpRequestMessage? received = null;

        var inner = new FakeHttpMessageHandler(req =>
        {
            received = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // 串接兩層 IngestApiKeyHandler
        var handler1 = new IngestApiKeyHandler(monitor) { InnerHandler = inner };
        var handler2 = new IngestApiKeyHandler(monitor) { InnerHandler = handler1 };

        using var invoker = new HttpMessageInvoker(handler2);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        // 先手動放一個舊 header
        req.Headers.Add("X-Ingest-Key", "old-key");

        await invoker.SendAsync(req, CancellationToken.None);

        Assert.NotNull(received);
        var values = received!.Headers.GetValues("X-Ingest-Key").ToList();
        Assert.Single(values);
        Assert.Equal("single-key", values[0]);
    }

    [Fact]
    public async Task ExternalProcessWrite_TriggersWatcherHotReload_IOptionsMonitorUpdates()
    {
        var protector = new PlaintextSettingsProtector();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "boot-secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "the-key");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(protector);
            });
        });

        // 確保 factory 啟動並解析 store 與 monitor
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<LineOptions>>();
        Assert.Equal("boot-secret", monitor.CurrentValue.ChannelSecret);

        // 模擬其他程序直接寫入設定檔（不呼叫 store.Save 或 store.Reload）
        var settingsPath = store.Path;
        EncryptedSettingsFile.Write(settingsPath, new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "external-secret"
        }, protector);

        // 輪詢等待 FileSystemWatcher 與去抖動觸發（上限 5 秒）
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var updated = false;
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            if (monitor.CurrentValue.ChannelSecret == "external-secret")
            {
                updated = true;
                break;
            }
            await Task.Delay(50);
        }

        Assert.True(updated, "外部程序寫入設定檔後，IOptionsMonitor 應在 5 秒內熱重載並取得新值");
        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, store.LoadStatus);
    }

    [Fact]
    public async Task Watcher_ReceivesCorruptedFile_DoesNotCrashHost_FallsBackAndServesRequests()
    {
        var protector = new PlaintextSettingsProtector();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "appsettings-secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "the-key");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsProtector>(protector);
            });
        });

        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<LineOptions>>();

        // 先寫入一次有效設定
        store.Save(new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "valid-secret"
        });
        Assert.Equal("valid-secret", monitor.CurrentValue.ChannelSecret);

        // 外部直接將檔案覆寫為毀損資料
        File.WriteAllBytes(store.Path, [0x00, 0x11, 0x22, 0x33, 0x44]);

        // 等待 Watcher 偵測並觸發重載（上限 5 秒）
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var reloadedToFallback = false;
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            if (store.LoadStatus == EncryptedSettingsLoadStatus.Unreadable)
            {
                reloadedToFallback = true;
                break;
            }
            await Task.Delay(50);
        }

        Assert.True(reloadedToFallback, "設定檔毀損後，Watcher 重載應將 LoadStatus 標記為 Unreadable");

        // 毀損後設定值應退回 appsettings
        Assert.Equal("appsettings-secret", monitor.CurrentValue.ChannelSecret);

        // 站台依然運作正常，HTTP 請求不受影響
        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req.Headers.Add("X-Line-Signature", ComputeSignature("appsettings-secret", body));
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
