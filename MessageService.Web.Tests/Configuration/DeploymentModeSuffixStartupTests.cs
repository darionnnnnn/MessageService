using MessageService.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

/// <summary>
/// 後綴設定檔的接線測試：DeploymentModeFileLocatorTests 只驗純函式，
/// 驗不到「後綴模式有沒有真的成為整個應用程式的生效模式」。
/// </summary>
public class DeploymentModeSuffixStartupTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), $"messageservice-suffix-startup-{Guid.NewGuid():N}");

    public DeploymentModeSuffixStartupTests()
    {
        Directory.CreateDirectory(_contentRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            try { Directory.Delete(_contentRoot, recursive: true); } catch { }
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(_contentRoot);
            builder.UseSetting("Deployment:Mode", "AllInOne");
            builder.UseSetting("EdgeAdmin:AllowPlaintextSettings", "true");
        });

    [Fact]
    public void SuffixFileWithoutModeKey_BecomesTheEffectiveModeForIOptionsConsumers()
    {
        // 後綴檔不寫 Deployment:Mode 是本機制的正常用法。模式若只寫進 Program.cs 的區域變數，
        // 透過 IOptions<DeploymentOptions> 讀模式的元件（EdgeChannelState、心跳回報、EdgeController）
        // 會讀到基底設定的 AllInOne——這裡以「基底宣告 AllInOne、後綴宣告 Edge」把兩者釘開
        File.WriteAllText(
            Path.Combine(_contentRoot, "appsettings.Testing.Edge.json"),
            """
            {
              "Line": { "ChannelSecret": "test-secret", "OutboundHere": false },
              "Ingest": { "BaseUrl": "https://db-host.example", "ApiKey": "test-key" }
            }
            """);

        using var factory = CreateFactory();

        var options = factory.Services.GetRequiredService<IOptions<DeploymentOptions>>();
        Assert.Equal(DeploymentMode.Edge, options.Value.Mode);
    }

    [Fact]
    public void TwoSuffixFiles_BlockStartup()
    {
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Testing.Edge.json"), "{}");
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Testing.Core.json"), "{}");

        using var factory = CreateFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.Services.GetService<IOptions<DeploymentOptions>>());
        Assert.Contains("appsettings.Testing.Edge.json", ex.ToString());
        Assert.Contains("appsettings.Testing.Core.json", ex.ToString());
    }

    [Fact]
    public void SuffixFileDeclaringADifferentMode_BlocksStartup()
    {
        File.WriteAllText(
            Path.Combine(_contentRoot, "appsettings.Testing.Edge.json"),
            """
            { "Deployment": { "Mode": "Core" } }
            """);

        using var factory = CreateFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.Services.GetService<IOptions<DeploymentOptions>>());
        Assert.Contains("appsettings.Testing.Edge.json", ex.ToString());
    }
}
