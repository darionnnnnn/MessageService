using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Tests.Services;

/// <summary>
/// 驗證不同拓撲（EdgeProxy vs Direct）下四個 LINE 具名 HttpClient 的 BaseAddress 註冊行為。
/// </summary>
public class LineHttpClientRegistrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"line-httpclient-reg-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:ChannelAccessToken", "token");
            builder.UseSetting("Line:OutboundHere", "true");
            builder.UseSetting("Ingest:BaseUrl", "https://core.example/");
            builder.UseSetting("Ingest:ApiKey", "test-key");
            builder.UseSetting("Heartbeat:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Outbox", $"Data Source={_dbPath}");
            configure(builder);
        });

    [Fact]
    public void EdgeProxy_ConfiguresExpectedBaseAddresses()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "EdgeProxy");
            builder.UseSetting("Line:OutboundProxyBaseUrl", "https://proxy.example/MSLine/");
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        var contentClient = factoryService.CreateClient(LineContentClient.HttpClientName);
        var stickerClient = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profileClient = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var imageClient = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Equal(new Uri("https://proxy.example/MSLine/line/data/"), contentClient.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/sticker/"), stickerClient.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/api/"), profileClient.BaseAddress);
        Assert.Null(imageClient.BaseAddress);
    }

    [Fact]
    public void Direct_LeavesAllBaseAddressesNull()
    {
        using var factory = CreateFactory(builder =>
        {
            builder.UseSetting("Line:OutboundVia", "Direct");
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        var contentClient = factoryService.CreateClient(LineContentClient.HttpClientName);
        var stickerClient = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profileClient = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var imageClient = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Null(contentClient.BaseAddress);
        Assert.Null(stickerClient.BaseAddress);
        Assert.Null(profileClient.BaseAddress);
        Assert.Null(imageClient.BaseAddress);
    }

    [Fact]
    public void HotReload_DirectToEdgeProxy_BaseAddressesUpdateDynamically()
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        });

        using var factory = CreateFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOptionsMonitor<LineOptions>>(monitor);
            });
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        // 1. Direct 設定下建立 client，BaseAddress 應為 null
        var content1 = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker1 = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile1 = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image1 = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Null(content1.BaseAddress);
        Assert.Null(sticker1.BaseAddress);
        Assert.Null(profile1.BaseAddress);
        Assert.Null(image1.BaseAddress);

        // 2. 切換為 EdgeProxy 設定
        monitor.CurrentValue = new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        };

        var content2 = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker2 = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile2 = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image2 = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Equal(new Uri("https://proxy.example/MSLine/line/data/"), content2.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/sticker/"), sticker2.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/api/"), profile2.BaseAddress);
        Assert.Null(image2.BaseAddress);
    }

    [Fact]
    public void HotReload_EdgeProxyToDirect_BaseAddressesUpdateDynamically()
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        });

        using var factory = CreateFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOptionsMonitor<LineOptions>>(monitor);
            });
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        // 1. EdgeProxy 設定下建立 client，BaseAddress 應為 proxy 位址
        var content1 = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker1 = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile1 = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image1 = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Equal(new Uri("https://proxy.example/MSLine/line/data/"), content1.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/sticker/"), sticker1.BaseAddress);
        Assert.Equal(new Uri("https://proxy.example/MSLine/line/api/"), profile1.BaseAddress);
        Assert.Null(image1.BaseAddress);

        // 2. 切換為 Direct 設定
        monitor.CurrentValue = new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        };

        var content2 = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker2 = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile2 = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image2 = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Null(content2.BaseAddress);
        Assert.Null(sticker2.BaseAddress);
        Assert.Null(profile2.BaseAddress);
        Assert.Null(image2.BaseAddress);
    }

    [Fact]
    public void EdgeProxy_WithEmptyOrWhitespaceBaseUrl_LeavesBaseAddressesNull()
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "   "
        });

        using var factory = CreateFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOptionsMonitor<LineOptions>>(monitor);
            });
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        var content = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Null(content.BaseAddress);
        Assert.Null(sticker.BaseAddress);
        Assert.Null(profile.BaseAddress);
        Assert.Null(image.BaseAddress);
    }

    [Fact]
    public void EdgeProxy_SubApplicationPath_PreservesTrailingSlashAndSubPath()
    {
        var monitor = new FakeOptionsMonitor<LineOptions>(new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "http://proxy.corp.local:8080/SubApp"
        });

        using var factory = CreateFactory(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOptionsMonitor<LineOptions>>(monitor);
            });
        });

        var factoryService = factory.Services.GetRequiredService<IHttpClientFactory>();

        var content = factoryService.CreateClient(LineContentClient.HttpClientName);
        var sticker = factoryService.CreateClient(LineContentClient.StickerHttpClientName);
        var profile = factoryService.CreateClient(LineProfileClient.HttpClientName);
        var image = factoryService.CreateClient(LineProfileClient.ImageHttpClientName);

        Assert.Equal(new Uri("http://proxy.corp.local:8080/SubApp/line/data/"), content.BaseAddress);
        Assert.Equal(new Uri("http://proxy.corp.local:8080/SubApp/line/sticker/"), sticker.BaseAddress);
        Assert.Equal(new Uri("http://proxy.corp.local:8080/SubApp/line/api/"), profile.BaseAddress);
        Assert.Null(image.BaseAddress);
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
