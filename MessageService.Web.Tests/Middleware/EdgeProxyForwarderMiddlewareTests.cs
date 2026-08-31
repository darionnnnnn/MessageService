using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MessageService.Options;
using MessageService.Tests.TestSupport;
using MessageService.Web.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Tests.Middleware;

public class EdgeProxyForwarderMiddlewareTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingLogger : ILogger<EdgeProxyForwarderMiddleware>
    {
        public int Warnings { get; private set; }
        public int Infos { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
            if (logLevel == LogLevel.Information) Infos++;
        }
    }

    private WebApplicationFactory<Program> CreateEdgeProxyFactory(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        string targetBaseUrl = "http://edge-host.example/MSLine",
        TimeProvider? timeProvider = null,
        ILogger<EdgeProxyForwarderMiddleware>? logger = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "EdgeProxy");
            builder.UseSetting("EdgeProxy:TargetBaseUrl", targetBaseUrl);

            builder.ConfigureServices(services =>
            {
                if (responder is not null)
                {
                    services.AddHttpClient(EdgeProxyOptions.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(responder));
                }

                if (timeProvider is not null)
                {
                    services.AddSingleton(timeProvider);
                }

                if (logger is not null)
                {
                    services.AddSingleton(logger);
                }
            });
        });
    }

    [Fact]
    public async Task WebhookForwarding_BodyIsPreservedByteForByte()
    {
        HttpRequestMessage? forwardedRequest = null;
        byte[]? forwardedBytes = null;

        using var factory = CreateEdgeProxyFactory(request =>
        {
            forwardedRequest = request;
            forwardedBytes = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        // 包含非 ASCII 字元、特殊跳脫字元、CRLF 等原始位元組
        var originalString = "{\"destination\":\"U123\",\"events\":[{\"type\":\"message\",\"text\":\"測試中文 123 !@#$%^&*()_+\\r\\n\\t\"}]}";
        var originalBytes = Encoding.UTF8.GetBytes(originalString);

        using var requestContent = new ByteArrayContent(originalBytes);
        requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/api/line/webhook", requestContent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);
        Assert.NotNull(forwardedBytes);
        Assert.Equal(originalBytes, forwardedBytes);
    }

    [Fact]
    public async Task WebhookForwarding_HeadersAllowlist_OnlyForwardsContentTypeAndSignature()
    {
        HttpRequestMessage? forwardedRequest = null;

        using var factory = CreateEdgeProxyFactory(request =>
        {
            forwardedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook");
        var originalBytes = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var byteContent = new ByteArrayContent(originalBytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        requestMessage.Content = byteContent;

        requestMessage.Headers.Add("X-Line-Signature", "test-signature-value-12345");
        requestMessage.Headers.Add("X-Should-Not-Be-Forwarded", "sensitive-internal-header");

        var response = await client.SendAsync(requestMessage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(forwardedRequest);

        // 白名單標頭：X-Line-Signature 與 Content-Type 必須存在且正確
        Assert.True(forwardedRequest.Headers.Contains("X-Line-Signature"));
        Assert.Equal("test-signature-value-12345", forwardedRequest.Headers.GetValues("X-Line-Signature").First());
        Assert.NotNull(forwardedRequest.Content?.Headers.ContentType);
        Assert.Equal("application/json", forwardedRequest.Content.Headers.ContentType.MediaType);

        // 非白名單標頭：不應存在
        Assert.False(forwardedRequest.Headers.Contains("X-Should-Not-Be-Forwarded"));
    }

    [Theory]
    [InlineData("http://edge-host.example/MSLine", "http://edge-host.example/MSLine/api/line/webhook")]
    [InlineData("http://edge-host.example/MSLine/", "http://edge-host.example/MSLine/api/line/webhook")]
    [InlineData("http://edge-host.example", "http://edge-host.example/api/line/webhook")]
    [InlineData("http://edge-host.example/", "http://edge-host.example/api/line/webhook")]
    public async Task WebhookForwarding_TargetUrl_PreservesBasePathAndSubAppPath(string targetBaseUrl, string expectedUrl)
    {
        Uri? requestedUri = null;

        using var factory = CreateEdgeProxyFactory(
            request =>
            {
                requestedUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            targetBaseUrl: targetBaseUrl);
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(requestedUri);
        Assert.Equal(expectedUrl, requestedUri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task WebhookForwarding_StatusCodePassthrough_ReturnsSameStatusCodeWithoutBody(HttpStatusCode edgeStatusCode)
    {
        using var factory = CreateEdgeProxyFactory(request =>
        {
            return new HttpResponseMessage(edgeStatusCode)
            {
                Content = new StringContent("Sensitive internal Edge error details should not be leaked")
            };
        });
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        Assert.Equal(edgeStatusCode, response.StatusCode);
        var responseBody = await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(responseBody);
    }

    [Fact]
    public async Task WebhookForwarding_HttpException_ReturnsBadGateway502()
    {
        using var factory = CreateEdgeProxyFactory(request =>
        {
            throw new HttpRequestException("Network failure to reach Edge host");
        });
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task WebhookForwarding_Timeout_ReturnsBadGateway502()
    {
        using var factory = CreateEdgeProxyFactory(request =>
        {
            throw new TaskCanceledException("HttpClient timeout occurred");
        });
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task WebhookForwarding_GetHealthz_IsNotForwardedAndReturnsOk()
    {
        var callCount = 0;
        using var factory = CreateEdgeProxyFactory(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        var healthzResponse = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthzResponse.StatusCode);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task WebhookForwarding_PostIngestEvents_IsNotForwardedAndReturnsNotFound()
    {
        var callCount = 0;
        using var factory = CreateEdgeProxyFactory(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        using var ingestContent = new StringContent("{}", Encoding.UTF8, "application/json");
        var ingestResponse = await client.PostAsync("/api/ingest/events", ingestContent);
        Assert.Equal(HttpStatusCode.NotFound, ingestResponse.StatusCode);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task WebhookForwarding_GetWebhook_IsNotForwarded()
    {
        var callCount = 0;
        using var factory = CreateEdgeProxyFactory(request =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();

        var getWebhookResponse = await client.GetAsync("/api/line/webhook");
        Assert.NotEqual(HttpStatusCode.OK, getWebhookResponse.StatusCode);
        Assert.True(getWebhookResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task WebhookForwarding_FailureLogThrottling_WarnsOnceThenEvery10MinutesThenInfoOnRecovery()
    {
        var timeProvider = new FakeTimeProvider();
        var logger = new CountingLogger();
        var shouldFail = true;

        using var factory = CreateEdgeProxyFactory(
            request =>
            {
                if (shouldFail)
                {
                    throw new HttpRequestException("Edge host is down");
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeProvider: timeProvider,
            logger: logger);
        using var client = factory.CreateClient();

        // 連續 20 次轉發失敗 -> Warning 恰 1 則
        for (var i = 0; i < 20; i++)
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/line/webhook", content);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        Assert.Equal(1, logger.Warnings);
        Assert.Equal(0, logger.Infos);

        // 時間推進超過 10 分鐘（11 分鐘）後再失敗一次 -> 第 2 則 Warning
        timeProvider.Now = timeProvider.Now.AddMinutes(11);
        using (var content = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var response = await client.PostAsync("/api/line/webhook", content);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        Assert.Equal(2, logger.Warnings);
        Assert.Equal(0, logger.Infos);

        // 之後成功一次 -> Information 1 則
        shouldFail = false;
        using (var content = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var response = await client.PostAsync("/api/line/webhook", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, logger.Warnings);
        Assert.Equal(1, logger.Infos);
    }

    [Fact]
    public async Task WebhookForwarding_ClientAbort_RethrowsOperationCanceledException()
    {
        using var factory = CreateEdgeProxyFactory(request =>
        {
            throw new OperationCanceledException();
        });
        using var client = factory.CreateClient();

        // 當 requestAborted 被取消時，OperationCanceledException 往外拋，
        // 由前置的 CancelledRequestMiddleware 攔截吞掉，回傳 200 OK 且不報錯
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        // 送出已被取消的請求
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.PostAsync("/api/line/webhook", content, cts.Token);
        });
    }
}
