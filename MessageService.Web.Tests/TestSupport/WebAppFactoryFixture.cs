using System.Net;
using MessageService.Data;
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
            // 避免吃到 appsettings.Development.json 預設的 AllowedClientIps（config 分層是疊加，
            // 疊上空陣列不會清掉底層既有的項目）。用一個沒有對應 appsettings.*.json 的環境名稱，
            // 讓 base appsettings.json（AllowedClientIps 預設就是 []）成為唯一基底，測試再自行疊加。
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            for (var i = 0; i < ips.Count; i++)
            {
                builder.UseSetting($"AllowedClientIps:{i}", ips[i]);
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
        await dbContext.SaveChangesAsync();
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

    private class FakeRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIp;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
