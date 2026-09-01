using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MessageService.Options;
using MessageService.Tests.TestSupport;
using MessageService.Web.Middleware;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Tests.Middleware;

public class EdgeProxyLineForwarderTests
{
    private WebApplicationFactory<Program> CreateFactory(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        string[]? allowedClientIps = null)
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
                // 模擬請求來源為 127.0.0.1
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));

                if (responder is not null)
                {
                    services.AddHttpClient(EdgeProxyLineForwarder.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(responder));
                }
            });
        });
    }

    [Theory]
    [InlineData("/line/api/v2/bot/group/G1/summary", "https://api.line.me/v2/bot/group/G1/summary")]
    [InlineData("/line/data/v2/bot/message/M1/content", "https://api-data.line.me/v2/bot/message/M1/content")]
    [InlineData("/line/sticker/123/android/sticker.png", "https://stickershop.line-scdn.net/123/android/sticker.png")]
    public async Task Get_FixedRoutes_ForwardsToExactTarget(string requestPath, string expectedTargetUrl)
    {
        HttpRequestMessage? forwardedRequest = null;

        using var factory = CreateFactory(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(requestPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);
        Assert.Equal(HttpMethod.Get, forwardedRequest.Method);
        Assert.Equal(expectedTargetUrl, forwardedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task Get_WithQueryString_ForwardsQueryString()
    {
        HttpRequestMessage? forwardedRequest = null;

        using var factory = CreateFactory(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/profile?userId=U123&rich=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);
        Assert.Equal("https://api.line.me/v2/bot/profile?userId=U123&rich=true", forwardedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task Get_HeadersForwarding_PassesAuthorization_DropsOtherCustomHeaders()
    {
        HttpRequestMessage? forwardedRequest = null;

        using var factory = CreateFactory(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
        using var client = factory.CreateClient();

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, "/line/api/v2/bot/info");
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-123");
        requestMessage.Headers.Add("X-Custom-Header", "should-be-dropped");

        var response = await client.SendAsync(requestMessage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);
        Assert.Equal("Bearer", forwardedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token-123", forwardedRequest.Headers.Authorization?.Parameter);
        Assert.False(forwardedRequest.Headers.Contains("X-Custom-Header"));
    }

    [Fact]
    public async Task Get_LineImage_AllowedHost_ForwardsSuccessfully()
    {
        HttpRequestMessage? forwardedRequest = null;

        using var factory = CreateFactory(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("image-data")
            };
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/image/profile.line-scdn.net/abc.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);
        Assert.Equal("https://profile.line-scdn.net/abc.jpg", forwardedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task Get_LineImage_DisallowedHost_ReturnsForbidden_HandlerNeverCalled()
    {
        var handlerCalled = false;

        using var factory = CreateFactory(request =>
        {
            handlerCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/image/evil.example.com/x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(handlerCalled, "開放代理防線：非允許清單網域不得呼叫後端 handler");
    }

    [Theory]
    // 路徑片段會被解碼，這些字元在拼進 URL 後會提前終止 host——
    // 只對原始字串做字尾比對的話，字尾檢查通過但實際連到的是 attacker.example
    [InlineData("attacker.example%23.line-scdn.net")]  // # 之後成為 fragment
    [InlineData("attacker.example%3F.line-scdn.net")]  // ? 之後成為 query
    [InlineData("attacker.example%2F.line-scdn.net")]  // / 之後成為 path
    [InlineData("attacker.example%40x.line-scdn.net")] // @ 之前成為 userinfo
    [InlineData("attacker.example%5C.line-scdn.net")]  // 反斜線在部分解析下等同路徑分隔
    public async Task Get_LineImage_HostWithUrlDelimiters_IsRejected(string encodedHost)
    {
        var handlerCalled = false;
        HttpRequestMessage? seen = null;

        using var factory = CreateFactory(request =>
        {
            handlerCalled = true;
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/line/image/{encodedHost}/abc.jpg");

        // 這是開放代理／SSRF 的實際攻擊形狀，必須在轉發前就擋掉
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(handlerCalled,
            $"SSRF 防線失守：{encodedHost} 通過了 host 檢查，實際會連到 {seen?.RequestUri?.Host}");
    }

    [Fact]
    public async Task Get_UpstreamStatusCode_IsPreserved()
    {
        using var factory = CreateFactory(request =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found")
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/group/G1/summary");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_UpstreamConnectionException_Returns502()
    {
        using var factory = CreateFactory(request =>
            throw new HttpRequestException("Connection refused"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/group/G1/summary");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private sealed class ChunkTrackingStream : MemoryStream
    {
        public int ReadChunkCount { get; private set; }

        public ChunkTrackingStream(byte[] buffer) : base(buffer) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await base.ReadAsync(buffer, cancellationToken);
            if (bytesRead > 0)
            {
                ReadChunkCount++;
            }
            return bytesRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = base.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                ReadChunkCount++;
            }
            return bytesRead;
        }
    }

    [Fact]
    public async Task Get_StreamContent_CopiesChunksWithoutBufferingWholeBody()
    {
        // 建立 128KB 資料，確認轉發中介層以串流形式 CopyToAsync 而非整包記憶體載入
        var data = new byte[128 * 1024];
        new Random(42).NextBytes(data);
        var trackingStream = new ChunkTrackingStream(data);

        using var factory = CreateFactory(request =>
        {
            var content = new StreamContent(trackingStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = data.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/data/v2/bot/message/M1/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(data.Length, response.Content.Headers.ContentLength);

        var receivedBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(data, receivedBytes);
        Assert.True(trackingStream.ReadChunkCount > 1, $"預期分塊讀取數次，實際次數：{trackingStream.ReadChunkCount}");
    }

    [Fact]
    public async Task EdgeProxy_AllowedClientIps_RejectsNonAllowedIpForLineProxy_AllowsWebhook()
    {
        HttpRequestMessage? webhookForwardedRequest = null;

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "EdgeProxy");
            builder.UseSetting("EdgeProxy:TargetBaseUrl", "http://192.0.2.10/MSLine");
            // 設成不含測試來源（127.0.0.1）的網段
            builder.UseSetting("EdgeProxy:AllowedClientIps:0", "192.0.2.0/24");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));

                services.AddHttpClient(EdgeProxyOptions.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(request =>
                    {
                        webhookForwardedRequest = request;
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }));

                services.AddHttpClient(EdgeProxyLineForwarder.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(request =>
                        new HttpResponseMessage(HttpStatusCode.OK)));
            });
        });
        using var client = factory.CreateClient();

        // 1. /line/* 路徑應回 403
        var lineResponse = await client.GetAsync("/line/api/v2/bot/info");
        Assert.Equal(HttpStatusCode.Forbidden, lineResponse.StatusCode);

        // 2. 同一設定下 /api/line/webhook 仍正常轉發（webhook 不吃 EdgeProxy:AllowedClientIps）
        var webhookContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var webhookResponse = await client.PostAsync("/api/line/webhook", webhookContent);

        Assert.Equal(HttpStatusCode.OK, webhookResponse.StatusCode);
        Assert.NotNull(webhookForwardedRequest);
    }

    [Fact]
    public async Task NonEdgeProxyMode_LineProxyEndpoint_ReturnsNotFound()
    {
        // 在 Edge 模式下，/line/api/* 不應有路由，應回 404
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Edge");
            builder.UseSetting("Ingest:BaseUrl", "https://core-host.example");
            builder.UseSetting("Ingest:ApiKey", "test-key");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:ChannelAccessToken", "token");
            builder.UseSetting("Line:OutboundHere", "false");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/group/G1/summary");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FakeDnsLookup(Func<string, IPAddress[]> factory) : IDnsLookup
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(factory(host));
    }

    private WebApplicationFactory<Program> CreateFactoryWithLogging(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        IDnsLookup dnsLookup,
        ILogger<EdgeProxyLineForwarder> logger)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "EdgeProxy");
            builder.UseSetting("EdgeProxy:TargetBaseUrl", "http://192.0.2.10/MSLine");
            builder.UseSetting("EdgeProxy:AllowedClientIps:0", "127.0.0.1");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1")));
                services.AddHttpClient(EdgeProxyLineForwarder.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(responder));
                services.AddSingleton(dnsLookup);
                services.AddSingleton(logger);
            });
        });
    }

    [Fact]
    public async Task Get_UpstreamException_LogsWarningWithTargetUrlAndResolvedIp()
    {
        var capturingLogger = new CapturingLogger<EdgeProxyLineForwarder>();
        var fakeDns = new FakeDnsLookup(_ => [IPAddress.Parse("203.0.113.10")]);

        using var factory = CreateFactoryWithLogging(
            _ => throw new HttpRequestException("Network failure"),
            fakeDns,
            capturingLogger);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/group/G1/summary");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var log = Assert.Single(capturingLogger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("https://api.line.me/v2/bot/group/G1/summary", log.Message);
        Assert.Contains("（IP：203.0.113.10）", log.Message);
    }

    [Fact]
    public async Task Get_UpstreamException_DnsFails_LogsWarningWithTargetUrlAndResolutionFailure()
    {
        var capturingLogger = new CapturingLogger<EdgeProxyLineForwarder>();
        var fakeDns = new FakeDnsLookup(_ => throw new System.Net.Sockets.SocketException());

        using var factory = CreateFactoryWithLogging(
            _ => throw new HttpRequestException("Network failure"),
            fakeDns,
            capturingLogger);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/line/api/v2/bot/group/G1/summary");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var log = Assert.Single(capturingLogger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("https://api.line.me/v2/bot/group/G1/summary", log.Message);
        Assert.Contains("（IP 解析失敗）", log.Message);
    }
}
