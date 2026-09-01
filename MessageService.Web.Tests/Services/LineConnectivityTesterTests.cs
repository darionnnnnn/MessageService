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
            Assert.Equal("/v2/bot/info", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"displayName\":\"我的機器人\",\"basicId\":\"@bot123\"}", Encoding.UTF8, "application/json")
            };
        });

        var options = new LineOptions
        {
            OutboundVia = LineOutboundVia.Direct
        };
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.True(result.Success);
        Assert.Equal("我的機器人", result.BotDisplayName);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("Direct", result.Via);
    }

    [Fact]
    public async Task TestConnectivityAsync_200WithBasicId_WhenDisplayNameMissing_ReturnsBasicId()
    {
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"basicId\":\"@bot123\"}", Encoding.UTF8, "application/json")
        });

        var options = new LineOptions();
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.True(result.Success);
        Assert.Equal("@bot123", result.BotDisplayName);
    }

    [Fact]
    public async Task TestConnectivityAsync_200WithEmptyJson_ReturnsEmptyDisplayName()
    {
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var options = new LineOptions();
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.True(result.Success);
        Assert.Equal("", result.BotDisplayName);
    }

    [Fact]
    public async Task TestConnectivityAsync_401Unauthorized_ReturnsFailure()
    {
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            ReasonPhrase = "Unauthorized",
            Content = new StringContent("{\"message\":\"Invalid OAuth access token\"}", Encoding.UTF8, "application/json")
        });

        var options = new LineOptions();
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.False(result.Success);
        Assert.Null(result.BotDisplayName);
        Assert.Contains("401", result.ErrorMessage);
        Assert.Contains("Invalid OAuth access token", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectivityAsync_Non2xxLongBody_TruncatesTo200Chars()
    {
        var longBody = new string('A', 300);
        var factory = CreateFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error",
            Content = new StringContent(longBody, Encoding.UTF8, "text/plain")
        });

        var options = new LineOptions();
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.False(result.Success);
        Assert.Contains(new string('A', 200), result.ErrorMessage);
        Assert.DoesNotContain(new string('A', 201), result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectivityAsync_WhenHandlerThrows_ReturnsFailureWithExceptionMessage()
    {
        var factory = CreateFactory(_ => throw new HttpRequestException("DNS resolution failed"));

        var options = new LineOptions();
        var tester = new LineConnectivityTester(factory, new FakeOptionsMonitor<LineOptions>(options));

        var result = await tester.TestConnectivityAsync();

        Assert.False(result.Success);
        Assert.Contains("HttpRequestException", result.ErrorMessage);
        Assert.Contains("DNS resolution failed", result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectivityAsync_WithOverrideToken_SetsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var factory = CreateFactory(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"displayName\":\"Bot\"}", Encoding.UTF8, "application/json")
            };
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
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"displayName\":\"Bot\"}", Encoding.UTF8, "application/json")
            };
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

        var result = await tester.TestConnectivityAsync();

        Assert.Equal(expectedVia, result.Via);
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
