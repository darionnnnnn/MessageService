using MessageService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }
}
