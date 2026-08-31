using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageService.Outbox;
using MessageService.Tests.TestSupport;
using MessageService.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageService.Web.Tests.Middleware;

public class WebhookSourceMiddlewareTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"messageservice-webhooksource-test-{Guid.NewGuid():N}");
    private readonly string _outboxDbPath;
    private readonly PlaintextSettingsProtector _protector = new();
    private const string Secret = "webhook-source-secret";

    public WebhookSourceMiddlewareTests()
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

    private WebApplicationFactory<Program> CreateFactory(
        Action<IWebHostBuilder>? configure = null,
        string clientIp = "127.0.0.1")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", Secret);
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "the-key");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");

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
    public async Task ModeAny_Default_AnySourceCanPass()
    {
        // Mode=Any（預設）-> 任何來源都能過（既有行為不變的驗收）
        using var factory = CreateFactory(builder =>
        {
            // WebhookSource:Mode 預設即為 Any
        }, clientIp: "192.0.2.99");

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        using var client = factory.CreateClient();
        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ModeAllowlistOnly_DisallowedSource_Returns403_AndOutboxCountUnchanged()
    {
        // Mode=AllowlistOnly + 來源不在清單 -> 403，且沒有寫進 outbox（斷言 outbox 筆數不變）
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("WebhookSource:Mode", "AllowlistOnly");
            builder.UseSetting("WebhookSource:AllowedIps:0", "192.0.2.1");
        }, clientIp: "192.0.2.99"); // 不在白名單中

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        int countBefore;
        using (var scope = factory.Services.CreateScope())
        {
            countBefore = await scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Entries.CountAsync();
        }

        using var client = factory.CreateClient();
        var payload = """
            {"destination":"d","events":[{
                "type":"message",
                "webhookEventId":"evt-blocked-source",
                "timestamp":1700000000000,
                "source":{"type":"group","groupId":"G1","userId":"U1"},
                "message":{"id":"m1","type":"text","text":"hello"}
            }]}
            """;
        var body = Encoding.UTF8.GetBytes(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        int countAfter;
        using (var scope = factory.Services.CreateScope())
        {
            countAfter = await scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Entries.CountAsync();
        }

        // 斷言 outbox 筆數不變
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task ModeAllowlistOnly_AllowedSource_Returns200WhenSignatureCorrect()
    {
        // Mode=AllowlistOnly + 來源在清單 -> 正常處理（簽章正確時 200）
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("WebhookSource:Mode", "AllowlistOnly");
            builder.UseSetting("WebhookSource:AllowedIps:0", "192.0.2.0/24");
        }, clientIp: "192.0.2.50"); // 在 CIDR 範圍內

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        using var client = factory.CreateClient();
        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ModeAllowlistOnly_EmptyAllowlist_BlocksAll()
    {
        // Mode=AllowlistOnly + 空清單 -> 全擋
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("WebhookSource:Mode", "AllowlistOnly");
            // AllowedIps 留空
        }, clientIp: "127.0.0.1");

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        using var client = factory.CreateClient();
        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        };
        req.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task HotReload_ChangeWebhookSourceMode_ImmediateEffectWithoutRebuildingHost()
    {
        // 熱生效：改 WebhookSource:Mode 後不重建 host 即生效
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("WebhookSource:Mode", "Any");
            builder.UseSetting("WebhookSource:AllowedIps:0", "192.0.2.1");
        }, clientIp: "192.0.2.99"); // 不在白名單中

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        using var client = factory.CreateClient();
        var payload = "{\"destination\":\"U123\",\"events\":[]}";
        var body = Encoding.UTF8.GetBytes(payload);

        // 1. 初始為 Any，來源 192.0.2.99 可通過回 200
        using (var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        })
        {
            req1.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));
            var res1 = await client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        }

        // 2. 熱更換 WebhookSource:Mode 為 AllowlistOnly
        var store = factory.Services.GetRequiredService<EdgeSettingsStore>();
        store.Save(new Dictionary<string, string?>
        {
            ["WebhookSource:Mode"] = "AllowlistOnly",
            ["WebhookSource:AllowedIps:0"] = "192.0.2.1"
        });

        // 3. 不重建 host，同一個 client 再次請求，立即被擋 403
        using (var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        })
        {
            req2.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));
            var res2 = await client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Forbidden, res2.StatusCode);
        }

        // 4. 再熱更換白名單把 192.0.2.99 加入
        store.Save(new Dictionary<string, string?>
        {
            ["WebhookSource:Mode"] = "AllowlistOnly",
            ["WebhookSource:AllowedIps:0"] = "192.0.2.99"
        });

        // 5. 立即放行回 200
        using (var req3 = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook")
        {
            Content = new ByteArrayContent(body)
        })
        {
            req3.Headers.Add("X-Line-Signature", ComputeSignature(Secret, body));
            var res3 = await client.SendAsync(req3);
            Assert.Equal(HttpStatusCode.OK, res3.StatusCode);
        }
    }
}
