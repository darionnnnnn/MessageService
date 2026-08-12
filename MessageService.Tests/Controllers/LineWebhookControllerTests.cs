using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageService.Data;
using MessageService.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Controllers;

public class LineWebhookControllerTests : IDisposable
{
    private const string ChannelSecret = "integration-test-secret";

    private readonly string _dbPath;
    private readonly string _outboxDbPath;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LineWebhookControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-test-{Guid.NewGuid():N}.db");
        _outboxDbPath = Path.Combine(Path.GetTempPath(), $"messageservice-test-outbox-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // 不用預設的 Development 環境：會自動載入這台開發機 user-secrets 裡一把真的 LINE
            // Channel Access Token，讓 Line:OutboundHere 預設 true 卻沒設 ChannelAccessToken
            // 的啟動驗證規則被意外滿足、測不出問題（只在這台機器「湊巧通過」，CI 會炸）；
            // 這份檔案測的是簽章驗證與 outbox 落地，跟媒體下載無關，直接關掉 OutboundHere。
            // 同時 appsettings.Development.json 對 Database:Provider 的 Sqlite 覆寫也一併
            // 消失，要明確設定，否則會落回 appsettings.json 的 SqlServer 預設值。
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxDbPath}");
            builder.UseSetting("Line:ChannelSecret", ChannelSecret);
        });

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
            scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.EnsureCreated();
        }

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _outboxDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string ComputeSignature(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(ChannelSecret), Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public async Task Post_WithoutSignature_Returns401()
    {
        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithInvalidSignature_Returns401()
    {
        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", "wrong-signature");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSignatureButMalformedBody_StillReturns200()
    {
        const string body = "{not valid json";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", ComputeSignature(body));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSignature_Returns200()
    {
        const string body = "{\"destination\":\"d\",\"events\":[]}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", ComputeSignature(body));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_ValidGroupTextMessage_LandsInDatabaseViaOutbox()
    {
        // 端到端驗證 Stage 0+1 的整條管線接對了：webhook → WebhookEventHandler 寫 outbox →
        // OutboxForwarderService（真的以背景服務身分在跑，不是手動呼叫）排空 → DirectIngestSink
        // 落地。用短輪詢等待而不是手動戳 forwarder，才是在測「這條線真的會自己動」。
        const string webhookEventId = "evt-e2e-1";
        var body = $$"""
            {"destination":"d","events":[{
                "type":"message",
                "webhookEventId":"{{webhookEventId}}",
                "timestamp":1700000000000,
                "source":{"type":"group","groupId":"G-E2E","userId":"U-E2E"},
                "message":{"id":"m-e2e-1","type":"text","text":"hello from e2e"}
            }]}
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", ComputeSignature(body));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            if (await dbContext.GroupMessages.AnyAsync(m => m.WebhookEventId == webhookEventId))
            {
                var saved = await dbContext.GroupMessages.SingleAsync(m => m.WebhookEventId == webhookEventId);
                Assert.Equal("hello from e2e", saved.Text);
                Assert.Equal("G-E2E", saved.GroupId);
                return;
            }
            await Task.Delay(50);
        }

        Assert.Fail("Message did not land in the database within the timeout — outbox forwarding did not run.");
    }
}
