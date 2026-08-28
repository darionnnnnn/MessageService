using MessageService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MessageService.Tests.Services;

/// <summary>EdgePullService 的註冊條件——沒設 Ingest:EdgeBaseUrl 的既有部署升級後不該
/// 多出一個背景服務（零設定升級的硬契約），起真實 host 驗證，不看註冊程式碼本身。</summary>
public class EdgePullServiceRegistrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"edgepull-reg-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Deployment:Mode", "Core");
            builder.UseSetting("Ingest:ApiKey", "test-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("Heartbeat:Enabled", "false");
            configure(builder);
        });

    private static bool HasEdgePullService(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<IHostedService>().Any(s => s is EdgePullService);

    [Fact]
    public void CoreMode_WithoutEdgeBaseUrl_DoesNotRegisterPullService()
    {
        using var factory = CreateFactory(_ => { });

        Assert.False(HasEdgePullService(factory));
    }

    [Fact]
    public void CoreMode_WithEdgeBaseUrl_RegistersPullService()
    {
        using var factory = CreateFactory(builder =>
            builder.UseSetting("Ingest:EdgeBaseUrl", "https://edge.example/"));

        Assert.True(HasEdgePullService(factory));
    }

    [Fact]
    public void CoreMode_WithWhitespaceEdgeBaseUrl_DoesNotRegisterPullService()
    {
        using var factory = CreateFactory(builder =>
            builder.UseSetting("Ingest:EdgeBaseUrl", "   "));

        Assert.False(HasEdgePullService(factory));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }
}
