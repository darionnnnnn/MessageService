using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class LineConnectivityTesterTests
{
    private static IHttpClientFactory CreateFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        return new FakeHttpClientFactory(handler);
    }

    /// <summary>測試一律用固定 IP 的假 DNS：真的去查 DNS 會讓測試依賴外部網路。</summary>
    private sealed class FakeDnsLookup(Func<string, IPAddress[]> factory) : IDnsLookup
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(factory(host));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
    }

    private static OutboundTargetResolver CreateResolver(string? ip = "203.0.113.10")
        => new(
            new FixedTimeProvider(),
            new FakeDnsLookup(_ => ip is null ? throw new System.Net.Sockets.SocketException() : [IPAddress.Parse(ip)]));

    private static LineConnectivityTester CreateTester(
        IHttpClientFactory factory,
        LineOptions options,
        OutboundTargetResolver? resolver = null,
        ILogger<LineConnectivityTester>? logger = null)
        => new(
            factory,
            new FakeOptionsMonitor<LineOptions>(options),
            resolver ?? CreateResolver(),
            logger ?? NullLogger<LineConnectivityTester>.Instance);

    [Fact]
    public async Task TestConnectivityAsync_200WithDisplayName_ReturnsSuccess()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"displayName\":\"我的機器人\",\"basicId\":\"@bot123\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(result.Success);
        Assert.Equal("我的機器人", result.Description);
        Assert.Equal("api.line.me", result.Target);
        Assert.Equal("Direct", result.Via);
    }

    [Fact]
    public async Task TestConnectivityAsync_200WithBasicId_WhenDisplayNameMissing_ReturnsBasicId()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"basicId\":\"@bot123\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions();
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(result.Success);
        Assert.Equal("@bot123", result.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_200WithEmptyJson_ShowsFallbackSuccessText()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions();
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(result.Success);
        Assert.Equal("連線成功", result.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_401Unauthorized_ReturnsFailure()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = "Unauthorized",
                    Content = new StringContent("{\"message\":\"Invalid OAuth access token\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions();
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.False(result.Success);
        Assert.Contains("401", result.Description);
        Assert.Contains("Line:ChannelAccessToken 無效或為空", result.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_WhenHandlerThrows_ReturnsFailureWithExceptionMessage()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                throw new HttpRequestException("DNS resolution failed");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions();
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.False(result.Success);
        Assert.Contains("HttpRequestException", result.Description);
        Assert.Contains("DNS resolution failed", result.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_WithOverrideToken_SetsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"displayName\":\"Bot\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { ChannelAccessToken = "configured-token" };
        var tester = CreateTester(factory, options);

        await tester.TestConnectivityAsync("my-override-token");

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-override-token", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TestConnectivityAsync_WithoutOverrideToken_DoesNotSetAuthorizationHeaderDirectly()
    {
        HttpRequestMessage? capturedRequest = null;
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"displayName\":\"Bot\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { ChannelAccessToken = "configured-token" };
        var tester = CreateTester(factory, options);

        await tester.TestConnectivityAsync(null);

        Assert.NotNull(capturedRequest);
        // 未提供覆寫 token 時，Tester 本身不填 Authorization，交給 LineAuthorizationHandler
        Assert.Null(capturedRequest!.Headers.Authorization);
    }

    [Theory]
    [InlineData(LineOutboundVia.EdgeProxy, "https://proxy.example/MSLine/", "EdgeProxy(https://proxy.example/MSLine/)")]
    [InlineData(LineOutboundVia.EdgeProxy, "", "EdgeProxy")]
    [InlineData(LineOutboundVia.Direct, null, "Direct")]
    public async Task TestConnectivityAsync_ReportsCorrectVia(LineOutboundVia via, string? proxyUrl, string expectedVia)
    {
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"Bot\"}", Encoding.UTF8, "application/json")
        });

        var options = new LineOptions
        {
            OutboundVia = via,
            OutboundProxyBaseUrl = proxyUrl
        };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(expectedVia, r.Via));
    }

    [Fact]
    public async Task TestConnectivityAsync_AllFourTargets_ReturnFourSuccessResults()
    {
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"displayName\":\"我的LINE機器人\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        Assert.Equal(["名稱查詢", "媒體內容", "貼圖", "頭貼 CDN"], results.Select(r => r.Purpose).ToArray());
        Assert.Equal(["api.line.me", "api-data.line.me", "stickershop.line-scdn.net", "profile.line-scdn.net"], results.Select(r => r.Target).ToArray());
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal("我的LINE機器人", results[0].Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_NameQuery401_OtherTargetsSucceed()
    {
        // 驗收標準 2：名稱查詢回 401 → 該列失敗且說明含分類器的 401 字串；其他三列不受影響
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = "Unauthorized"
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        var nameResult = results.First(r => r.Purpose == "名稱查詢");
        Assert.False(nameResult.Success);
        Assert.Contains("401", nameResult.Description);
        Assert.Contains("Line:ChannelAccessToken 無效或為空", nameResult.Description);

        var otherResults = results.Where(r => r.Purpose != "名稱查詢").ToList();
        Assert.Equal(3, otherResults.Count);
        Assert.All(otherResults, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task TestConnectivityAsync_Content404_MarkedAsReachable()
    {
        // 驗收標準 3：媒體內容回 404 → 該列判定為可達（不是失敗）
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/probe")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found"
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var contentResult = results.First(r => r.Purpose == "媒體內容");
        Assert.True(contentResult.Success);
        Assert.Contains("HTTP 404", contentResult.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_StickerTarget_SendsCorrectStickerPath()
    {
        // 驗收標準 4：貼圖那筆的請求 URI 實際打的是貼圖路徑（斷言假 handler 收到的 URI）
        var capturedRequests = new List<HttpRequestMessage>();
        var factory = CreateFactory(req =>
        {
            capturedRequests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var stickerReq = capturedRequests.FirstOrDefault(r => r.RequestUri?.AbsolutePath.Contains("stickershop/v1/sticker") == true);
        Assert.NotNull(stickerReq);
        Assert.Equal("/stickershop/v1/sticker/52002734/android/sticker.png", stickerReq!.RequestUri?.AbsolutePath);

        var stickerResult = results.First(r => r.Purpose == "貼圖");
        Assert.True(stickerResult.Success);
    }

    [Fact]
    public async Task TestConnectivityAsync_ProfileImage_UnderEdgeProxy_RewritesToProxyPath()
    {
        // 驗收標準 5：頭貼那筆在 EdgeProxy 設定下，實際請求 URI 是 proxy 的 /line/image/ 路徑
        var capturedRequests = new List<HttpRequestMessage>();
        var factory = CreateFactory(req =>
        {
            capturedRequests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        var imageReq = capturedRequests.FirstOrDefault(r => r.RequestUri?.AbsolutePath.Contains("/line/image/") == true);
        Assert.NotNull(imageReq);
        Assert.Equal("https://proxy.example/MSLine/line/image/profile.line-scdn.net/probe", imageReq!.RequestUri?.ToString());

        var imageResult = results.First(r => r.Purpose == "頭貼 CDN");
        Assert.True(imageResult.Success);
    }

    [Fact]
    public async Task TestConnectivityAsync_WhenOneTargetThrowsConnectionException_OtherTargetsContinue()
    {
        // 驗收標準 6：某一筆丟連線例外時，該列顯示不可達與分類字串，且不影響其他筆
        var factory = CreateFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("stickershop") == true)
            {
                throw new TimeoutException("Connection to sticker CDN timed out");
            }
            if (req.RequestUri?.AbsolutePath == "/v2/bot/info")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"displayName\":\"機器人\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        var stickerResult = results.First(r => r.Purpose == "貼圖");
        Assert.False(stickerResult.Success);
        Assert.Contains("連線逾時", stickerResult.Description);

        var nameResult = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(nameResult.Success);
        var contentResult = results.First(r => r.Purpose == "媒體內容");
        Assert.True(contentResult.Success);
        var imageResult = results.First(r => r.Purpose == "頭貼 CDN");
        Assert.True(imageResult.Success);
    }

    [Fact]
    public async Task LineAuthorizationHandler_WhenAuthorizationAlreadySet_DoesNotOverwrite()
    {
        var options = new LineOptions { ChannelAccessToken = "configured-token" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);

        HttpRequestMessage? receivedRequest = null;
        var innerHandler = new FakeHttpMessageHandler(req =>
        {
            receivedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(authHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "override-token");

        await client.SendAsync(request);

        Assert.NotNull(receivedRequest);
        Assert.Equal("Bearer", receivedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("override-token", receivedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task LineAuthorizationHandler_WhenAuthorizationNull_InjectsConfiguredToken()
    {
        var options = new LineOptions { ChannelAccessToken = "configured-token" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);

        HttpRequestMessage? receivedRequest = null;
        var innerHandler = new FakeHttpMessageHandler(req =>
        {
            receivedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(authHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info");

        await client.SendAsync(request);

        Assert.NotNull(receivedRequest);
        Assert.Equal("Bearer", receivedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("configured-token", receivedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TestConnectivityAsync_EdgeProxyTopology_TargetShowsProxyHostNotLineDomain()
    {
        // 走 EdgeProxy 時實際連的是 proxy——Target 顯示 LINE 的網域會讓人去開錯誤的防火牆洞
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal("proxy.example", r.Target));
    }

    [Fact]
    public async Task TestConnectivityAsync_OnlyNameQueryUsesStrictSuccess()
    {
        // StrictSuccess 決定頁面顯示「成功／失敗」還是「可達／不可達」——
        // 只有名稱查詢那列以 2xx 為判準，其餘三列是連通性判準
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"bot\"}", Encoding.UTF8, "application/json")
        });
        var tester = CreateTester(factory, new LineOptions { OutboundVia = LineOutboundVia.Direct });

        var results = await tester.TestConnectivityAsync();

        Assert.True(results[0].StrictSuccess);
        Assert.All(results.Skip(1), r => Assert.False(r.StrictSuccess));
    }

    [Fact]
    public async Task TestConnectivityAsync_Direct_ReportsAbsoluteRequestUrlAndResolvedIp()
    {
        // 這張表是拿去跟網管核對防火牆的：成功列也要看得到實際網址與 IP
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"bot\"}", Encoding.UTF8, "application/json")
        });
        var tester = CreateTester(factory, new LineOptions { OutboundVia = LineOutboundVia.Direct });

        var results = await tester.TestConnectivityAsync();

        // FakeHttpClientFactory 會預設 BaseAddress，所以這裡只驗「組出來的是絕對網址、
        // 且帶著該列真正要打的路徑」——網域由 BaseAddress 決定，那是註冊層的事
        Assert.All(results, r => Assert.True(Uri.IsWellFormedUriString(r.RequestUrl, UriKind.Absolute), r.RequestUrl));
        Assert.EndsWith("/v2/bot/info", results.First(r => r.Purpose == "名稱查詢").RequestUrl);
        Assert.EndsWith("/probe", results.First(r => r.Purpose == "媒體內容").RequestUrl);
        Assert.All(results, r => Assert.Equal("203.0.113.10", r.ResolvedIp));
    }

    [Fact]
    public async Task TestConnectivityAsync_EdgeProxy_RequestUrlPointsAtProxyNotLine()
    {
        // EdgeProxy 拓撲下要開通的是 proxy 的網址，顯示 LINE 的網址會讓人去開錯誤的洞
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"bot\"}", Encoding.UTF8, "application/json")
        });
        var options = new LineOptions
        {
            OutboundVia = LineOutboundVia.EdgeProxy,
            OutboundProxyBaseUrl = "https://proxy.example/MSLine/"
        };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync();

        // 頭貼那列打的是絕對 URL（由 LineImageUrlRewriter 改寫），不受測試替身的 BaseAddress 影響，
        // 是這裡唯一驗得到「真的指向 proxy」的一列
        var image = results.First(r => r.Purpose == "頭貼 CDN");
        // 原 LINE 網域留在路徑裡是改寫規則的一部分（proxy 靠它決定往哪個 CDN 轉），
        // 重點是主機部分換成了 proxy
        Assert.StartsWith("https://proxy.example/MSLine/line/image/", image.RequestUrl);
        Assert.Equal("proxy.example", new Uri(image.RequestUrl).Host);
        // 其餘各列的 Target 走 proxy host——這是防火牆要開通的對象
        Assert.All(results, r => Assert.Equal("proxy.example", r.Target));
    }

    [Fact]
    public async Task TestConnectivityAsync_DnsFails_ResolvedIpIsNullButTestStillRuns()
    {
        // DNS 解析只影響顯示，不該把連得上的目標判成失敗
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"bot\"}", Encoding.UTF8, "application/json")
        });
        var tester = CreateTester(
            factory, new LineOptions { OutboundVia = LineOutboundVia.Direct }, CreateResolver(ip: null));

        var results = await tester.TestConnectivityAsync();

        Assert.All(results, r => Assert.Null(r.ResolvedIp));
        Assert.True(results.First(r => r.Purpose == "名稱查詢").Success);
    }

    [Fact]
    public async Task TestConnectivityAsync_HttpStatusFailure_DescriptionCarriesTargetHost()
    {
        // 4xx／5xx 的說明以前不帶目標，拿去問網管沒東西可對
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var tester = CreateTester(factory, new LineOptions { OutboundVia = LineOutboundVia.Direct });

        var results = await tester.TestConnectivityAsync();

        var content = results.First(r => r.Purpose == "媒體內容");
        Assert.False(content.Success);
        Assert.Contains("api-data.line.me", content.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_NameQuery5xx_DescriptionCarriesTargetHost()
    {
        // 名稱查詢那列的狀態碼判定跟其他三列是各自獨立的一份，要分別驗到。
        // 用 500 而不是 401：401/403/404/429 是「token 或路徑」問題，分類器刻意不帶 host
        var factory = CreateFactory(req => req.RequestUri?.AbsolutePath == "/v2/bot/info"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK));
        var tester = CreateTester(factory, new LineOptions { OutboundVia = LineOutboundVia.Direct });

        var results = await tester.TestConnectivityAsync();

        var nameQuery = results.First(r => r.Purpose == "名稱查詢");
        Assert.False(nameQuery.Success);
        Assert.Contains("api.line.me", nameQuery.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_Failure_LogsWarningWithUrlAndIp()
    {
        // 沒開著頁面時也要留得下紀錄，網管事後才查得到是哪個網址／IP 不通
        var logger = new CapturingLogger<LineConnectivityTester>();
        var factory = CreateFactory(_ => throw new HttpRequestException("連不上"));
        var tester = CreateTester(
            factory, new LineOptions { OutboundVia = LineOutboundVia.Direct }, logger: logger);

        await tester.TestConnectivityAsync();

        var warnings = logger.Logs.Where(l => l.Level == LogLevel.Warning).ToList();
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("api.line.me")
            && w.Message.Contains("203.0.113.10")
            && w.Message.Contains("/v2/bot/info"));
    }

    [Fact]
    public async Task TestConnectivityAsync_AllSucceed_LogsNothing()
    {
        var logger = new CapturingLogger<LineConnectivityTester>();
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"displayName\":\"bot\"}", Encoding.UTF8, "application/json")
        });
        var tester = CreateTester(
            factory, new LineOptions { OutboundVia = LineOutboundVia.Direct }, logger: logger);

        await tester.TestConnectivityAsync();

        Assert.Empty(logger.Logs);
    }

    [Fact]
    public async Task TestConnectivityAsync_InternalTimeout_TaskCanceledException_ReturnsTimeoutDescriptionWithoutCallerInterrupted()
    {
        var factory = CreateFactory(_ => throw new TaskCanceledException("The operation was canceled."));
        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Success);
            Assert.Contains("連線逾時", r.Description);
            Assert.Contains(r.Target, r.Description);
            Assert.DoesNotContain("呼叫端中斷", r.Description);
        });
    }

    [Fact]
    public async Task TestConnectivityAsync_ExternalTokenCancelled_ReturnsCancelledClassification()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var factory = CreateFactory(_ => throw new TaskCanceledException("The operation was canceled."));
        var options = new LineOptions { OutboundVia = LineOutboundVia.Direct };
        var tester = CreateTester(factory, options);

        var results = await tester.TestConnectivityAsync(cancellationToken: cts.Token);

        Assert.Equal(4, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Success);
            Assert.Contains("請求已取消", r.Description);
            Assert.Contains("呼叫端中斷", r.Description);
        });
    }
}
