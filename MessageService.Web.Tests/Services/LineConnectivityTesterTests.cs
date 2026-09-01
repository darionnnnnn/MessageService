using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(result.Success);
        Assert.Equal("@bot123", result.Description);
    }

    [Fact]
    public async Task TestConnectivityAsync_200WithEmptyJson_ReturnsEmptyDisplayName()
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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var results = await tester.TestConnectivityAsync();

        var result = results.First(r => r.Purpose == "名稱查詢");
        Assert.True(result.Success);
        Assert.Equal("", result.Description);
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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance)
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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance)
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
}
