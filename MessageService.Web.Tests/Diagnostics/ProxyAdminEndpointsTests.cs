using System.Net;
using System.Net.Http.Json;
using MessageService.Options;
using MessageService.Tests.TestSupport;
using MessageService.Web.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MessageService.Web.Tests.Diagnostics;

public class ProxyAdminEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-proxyadmin-test-{Guid.NewGuid():N}.db");
    private readonly string _outboxPath = Path.Combine(Path.GetTempPath(), $"messageservice-proxyadmin-outbox-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _outboxPath })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private WebApplicationFactory<Program> CreateEdgeProxyFactory(
        string[]? allowedClientIps = null,
        string clientIp = "127.0.0.1")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "EdgeProxy");
            builder.UseSetting("EdgeProxy:TargetBaseUrl", "http://192.0.2.10/MSLine");

            if (allowedClientIps is not null)
            {
                for (var i = 0; i < allowedClientIps.Length; i++)
                {
                    builder.UseSetting($"EdgeProxy:AllowedClientIps:{i}", allowedClientIps[i]);
                }
            }
            else
            {
                // 預設允許 127.0.0.1 方便一般測試
                builder.UseSetting("EdgeProxy:AllowedClientIps:0", "127.0.0.1");
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse(clientIp)));
            });
        });
    }

    [Fact]
    public async Task GetErrors_EdgeProxyMode_AllowedIp_Returns200WithMachineNameAndEntriesAndNoStoreHeader()
    {
        using var factory = CreateEdgeProxyFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/proxy-admin/errors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 驗證 Cache-Control: no-store 標頭
        var cacheControl = response.Headers.CacheControl?.ToString();
        Assert.NotNull(cacheControl);
        Assert.Contains("no-store", cacheControl);

        var body = await response.Content.ReadFromJsonAsync<ProxyAdminErrorsResponse>();
        Assert.NotNull(body);
        Assert.Equal(Environment.MachineName, body.MachineName);
        Assert.True(body.ProcessStartTimeUtc <= DateTimeOffset.UtcNow);
        Assert.NotNull(body.Entries);
    }

    [Fact]
    public async Task GetErrors_EdgeProxyMode_DisallowedIp_Returns403Forbidden()
    {
        // 白名單設為 192.0.2.1，但請求來源為 127.0.0.1
        using var factory = CreateEdgeProxyFactory(
            allowedClientIps: ["192.0.2.1"],
            clientIp: "127.0.0.1");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/proxy-admin/errors");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetErrors_EdgeProxyMode_EmptyAllowlist_Returns403Forbidden()
    {
        using var factory = CreateEdgeProxyFactory(
            allowedClientIps: [""],
            clientIp: "127.0.0.1");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/proxy-admin/errors");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Edge")]
    [InlineData("AllInOne")]
    [InlineData("Core")]
    [InlineData("Viewer")]
    public async Task GetErrors_NonEdgeProxyModes_Returns404NotFound(string mode)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", mode);
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Viewer:AllowedClientIps:0", "127.0.0.1");

            if (mode == "Edge")
            {
                builder.UseSetting("Line:ChannelSecret", "secret");
                builder.UseSetting("Line:ChannelAccessToken", "token");
                builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
                builder.UseSetting("Ingest:ApiKey", "test-key");
                builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxPath}");
                builder.UseSetting("EdgeAdmin:AllowPlaintextSettings", "true");
                builder.UseSetting("EdgeAdmin:AllowedClientIps:0", "127.0.0.1");
            }
            else
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
                builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_outboxPath}");
                if (mode == "AllInOne")
                {
                    builder.UseSetting("Line:ChannelSecret", "secret");
                }
                else if (mode == "Core")
                {
                    builder.UseSetting("Ingest:ApiKey", "the-key");
                    builder.UseSetting("Ingest:AllowedClientIps:0", "127.0.0.1");
                }
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/proxy-admin/errors");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetErrors_EdgeProxyMode_WhenWarningAndErrorLogged_ReturnsBufferEntriesInOrder()
    {
        using var factory = CreateEdgeProxyFactory();
        using var client = factory.CreateClient();

        // 透過 DI 容器取得 logger 寫入 warning 與 error
        var logger = factory.Services.GetRequiredService<ILogger<ProxyAdminEndpointsTests>>();
        logger.LogWarning("First proxy warning test");
        logger.LogError(new InvalidOperationException("Upstream connection failed"), "Second proxy error test");

        var response = await client.GetAsync("/proxy-admin/errors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ProxyAdminErrorsResponse>();
        Assert.NotNull(body);
        Assert.True(body.Entries.Count >= 2);

        // 驗證最新記錄排在最前面 (新到舊排序)
        var first = body.Entries[0];
        Assert.Equal("Second proxy error test", first.Message);
        Assert.Equal(LogLevel.Error, first.Level);
        Assert.NotNull(first.ExceptionSummary);
        Assert.Contains("System.InvalidOperationException: Upstream connection failed", first.ExceptionSummary);

        var second = body.Entries[1];
        Assert.Equal("First proxy warning test", second.Message);
        Assert.Equal(LogLevel.Warning, second.Level);
        Assert.Null(second.ExceptionSummary);
    }
}