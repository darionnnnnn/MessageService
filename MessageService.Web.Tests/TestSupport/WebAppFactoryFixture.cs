using System.Net;
using MessageService.Data;
using MessageService.Models;
using MessageService.Services;
using MessageService.Web.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Web.Tests.TestSupport;

public class WebAppFactoryFixture : IDisposable
{
    private readonly string _dbPath;

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public string DbConnectionString => $"Data Source={_dbPath}";

    public WebAppFactoryFixture(IReadOnlyList<string>? allowedClientIps = null, string? encryptionKey = null)
    {
        var ips = allowedClientIps ?? ["127.0.0.1", "::1"];
        _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-web-test-{Guid.NewGuid():N}.db");

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // 避免吃到 appsettings.Development.json 預設的 Viewer:AllowedClientIps（config 分層是
            // 疊加，疊上空陣列不會清掉底層既有的項目）。用一個沒有對應 appsettings.*.json 的環境
            // 名稱，讓 base appsettings.json（Viewer:AllowedClientIps 預設就是 []）成為唯一基底，
            // 測試再自行疊加。
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            // 合併後 Program.cs 在啟動時一律跑 DeploymentValidator——這裡的測試只關心檢視端
            // 行為，跟 webhook／ingest 無關，用 Db 模式（有資料庫、不收 webhook）滿足驗證
            // 又不需要真的設 LINE 密鑰；OutboundHere 關掉避免連帶要求 ChannelAccessToken。
            // Ingest:ApiKey 給假值只是滿足 Db 模式的啟動驗證，這裡的測試不會真的打 ingest 端點。
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "webappfactoryfixture-unused-key");
            builder.UseSetting("Line:OutboundHere", "false");
            // HeartbeatService 啟動就立刻寫一筆 HostHeartbeats（見 HeartbeatOptions.Enabled
            // 說明），會跟測試自己準備的心跳資料互相污染；其他背景服務都能靠既有能力開關
            // （OutboundHere／ReceivesWebhook）在這個 fixture 的設定下自然關閉，心跳沒有
            // 現成開關可借，所以額外關掉
            builder.UseSetting("Heartbeat:Enabled", "false");
            for (var i = 0; i < ips.Count; i++)
            {
                builder.UseSetting($"Viewer:AllowedClientIps:{i}", ips[i]);
            }

            if (encryptionKey is not null)
            {
                builder.UseSetting("Encryption:Enabled", "true");
                builder.UseSetting("Encryption:Key", encryptionKey);
            }

            // TestServer 的請求沒有真正的 TCP 連線，Connection.RemoteIpAddress 預設是 null，
            // IpAllowlistMiddleware 會把 null 一律當成拒絕。用 IStartupFilter 在管線最前面
            // 補一個固定的來源 IP（127.0.0.1），讓測試可以用 AllowedClientIps 控制通過與否。
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1"))));
        });

        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
        }

        Client = Factory.CreateClient();
    }

    public async Task SeedAsync(Func<MessageDbContext, Task> seed)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        await seed(dbContext);

        // 側欄改讀 Groups.LastMessageId／LastMessageAt（見 GroupsController），這裡在存檔前
        // 先記下這次呼叫新增了哪些 GroupMessage，存檔後（Id 已由 EF 填回）比照 DirectIngestSink
        // 落地時的邏輯自動維護——跟正式寫入路徑共用同一份 GroupLastMessageTracker，測試不用
        // 每個案例自己顧到這兩個欄位，也不會有另一份簡化版邏輯跟正式路徑漂移的風險
        var addedMessages = dbContext.ChangeTracker.Entries<GroupMessage>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        await dbContext.SaveChangesAsync();

        foreach (var message in addedMessages)
        {
            await GroupLastMessageTracker.TrackAsync(
                dbContext, message.GroupId, message.Id, message.EventTimestamp, CancellationToken.None);
        }
        if (addedMessages.Count > 0)
        {
            await dbContext.SaveChangesAsync();
        }

        // 遮蔽規則有 30 秒的程序內快取（見 MaskingService），而這裡是直接寫資料庫、繞過
        // SettingsController 的失效呼叫。測試若在 seed 之前就打過任何會載入規則的端點，
        // 之後改的 ViewerSettings／MaskKeywords／UserAliases 都不會生效，症狀是斷言拿到
        // 舊的顯示名稱——而且只在那個先後順序下才發生，很難從失敗訊息看出原因。
        // 在這裡一併失效，讓 seed 對測試而言維持「寫進去就算數」的直覺語意。
        scope.ServiceProvider.GetService<IMaskingService>()?.InvalidateCache();
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
