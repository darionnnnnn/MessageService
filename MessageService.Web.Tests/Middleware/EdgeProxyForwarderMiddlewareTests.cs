using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MessageService.Options;
using MessageService.Tests.TestSupport;
using MessageService.Web.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
        public List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
                WarningMessages.Add(formatter(state, exception));
            }
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
    public async Task Middleware_HttpClientTimeout_WithoutClientAbort_Returns502NotRethrow()
    {
        // HttpClient 逾時丟的 TaskCanceledException 用的是內部 token——RequestAborted 沒被取消
        // 時必須走 502 路徑，不能被誤判成客戶端中斷往外拋。直測 middleware 才控制得住
        // RequestAborted 的狀態（TestServer 模擬不了真實斷線）
        var middleware = CreateMiddleware(
            _ => throw new TaskCanceledException("HttpClient timeout"),
            out _, out _);

        var context = NewWebhookContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_ClientAborted_RethrowsForCancelledRequestMiddleware()
    {
        // 客戶端主動斷線：RequestAborted 已取消、SendAsync 丟 OperationCanceledException——
        // 必須原樣往外拋（交給 CancelledRequestMiddleware），不能吞成 502
        var middleware = CreateMiddleware(
            _ => throw new OperationCanceledException(),
            out _, out _);

        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var context = NewWebhookContext();
        context.RequestAborted = aborted.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task Middleware_TrailingSlash_IsStillForwarded()
    {
        // LINE Console 的 URL 人手多打一個尾斜線很常見；真正的 Edge 主機路由對它寬容，
        // proxy 不比對就會變成「直收正常、經 proxy 整批 404」的難查差異
        var forwarded = 0;
        var middleware = CreateMiddleware(
            _ => { Interlocked.Increment(ref forwarded); return new HttpResponseMessage(HttpStatusCode.OK); },
            out _, out _);

        var context = NewWebhookContext(path: "/api/line/webhook/");

        await middleware.InvokeAsync(context);

        Assert.Equal(1, forwarded);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_SetsRequestBodySizeLimit_OnWebhookPath()
    {
        // 公網端點無身分驗證，body 上限是唯一的記憶體防線——斷言 middleware 真的把
        // IHttpMaxRequestBodySizeFeature 夾到 512KB
        var middleware = CreateMiddleware(
            _ => new HttpResponseMessage(HttpStatusCode.OK), out _, out _);

        var sizeFeature = new RecordingBodySizeFeature();
        var context = NewWebhookContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(sizeFeature);

        await middleware.InvokeAsync(context);

        Assert.Equal(512 * 1024, sizeFeature.MaxRequestBodySize);
    }

    private static DefaultHttpContext NewWebhookContext(string path = "/api/line/webhook")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        return context;
    }

    private sealed class RecordingBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private static EdgeProxyForwarderMiddleware CreateMiddleware(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out FakeTimeProvider timeProvider, out CountingLogger logger)
    {
        timeProvider = new FakeTimeProvider();
        logger = new CountingLogger();
        var handler = new FakeHttpMessageHandler(responder);
        var factory = new StubHttpClientFactory(handler);
        return new EdgeProxyForwarderMiddleware(
            _ => Task.CompletedTask, factory, timeProvider, logger);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://edge-host.example/MSLine/") };
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
        // EdgeProxy 下 LineWebhookController 已被 DeploymentModeConvention 移除，404 是唯一正確答案
        Assert.Equal(HttpStatusCode.NotFound, getWebhookResponse.StatusCode);
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
    public async Task Middleware_Flapping_LogVolumeIsBounded()
    {
        // Edge 半死不活（時好時壞）是最需要節流的情境：失敗→成功交錯 40 次，
        // 若每次「轉失敗」都記完整堆疊、每次「轉成功」都記恢復，log 會以請求頻率被灌爆
        var fail = false;
        var middleware = CreateMiddleware(
            _ => (fail = !fail)
                ? throw new HttpRequestException("flap")
                : new HttpResponseMessage(HttpStatusCode.OK),
            out var time, out var logger);

        for (var i = 0; i < 40; i++)
        {
            await middleware.InvokeAsync(NewWebhookContext());
        }

        Assert.Equal(1, logger.Warnings);
        Assert.Equal(1, logger.Infos);

        // 過了節流窗口，flapping 仍在繼續 → 各多一則，讓維運知道問題還沒好
        time.Now = time.Now.AddMinutes(11);
        for (var i = 0; i < 4; i++)
        {
            await middleware.InvokeAsync(NewWebhookContext());
        }

        Assert.Equal(2, logger.Warnings);
        Assert.Equal(2, logger.Infos);
    }

    [Fact]
    public async Task WebhookForwarding_HttpException_LogsWarningWithTargetDescription()
    {
        var logger = new CountingLogger();
        using var factory = CreateEdgeProxyFactory(
            _ => throw new HttpRequestException("Network failure to reach Edge host"),
            targetBaseUrl: "http://edge-host.example/MSLine",
            logger: logger);
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, logger.Warnings);
        var msg = Assert.Single(logger.WarningMessages);
        Assert.Contains("http://edge-host.example/MSLine/api/line/webhook", msg);
    }

    [Fact]
    public async Task Middleware_NoBaseAddress_Failure_LogsWarningWithMissingTargetDescription()
    {
        var logger = new CountingLogger();
        var timeProvider = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Network failure"));
        var client = new HttpClient(handler, disposeHandler: false);
        var factory = new DirectHttpClientFactory(client);
        var middleware = new EdgeProxyForwarderMiddleware(
            _ => Task.CompletedTask, factory, timeProvider, logger);

        var context = NewWebhookContext();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(1, logger.Warnings);
        var msg = Assert.Single(logger.WarningMessages);
        Assert.Contains("未設定 EdgeProxy 目標位址", msg);
    }

    private sealed class DirectHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
