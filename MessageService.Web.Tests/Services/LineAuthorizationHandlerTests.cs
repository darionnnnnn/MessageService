using System.Net;
using System.Net.Http.Headers;
using MessageService.Options;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class LineAuthorizationHandlerTests
{
    private sealed class CapturingLogger : ILogger<LineAuthorizationHandler>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task SendAsync_WhenTokenEmptyString_DoesNotAddAuthorizationHeader()
    {
        var options = new LineOptions { ChannelAccessToken = "" };
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
        Assert.Null(receivedRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_WhenTokenWhitespace_DoesNotAddAuthorizationHeader()
    {
        var options = new LineOptions { ChannelAccessToken = "   " };
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
        Assert.Null(receivedRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_WhenTokenHasValue_InjectsAuthorizationHeader()
    {
        var options = new LineOptions { ChannelAccessToken = "valid-token-123" };
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
        Assert.Equal("valid-token-123", receivedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SendAsync_WhenAuthorizationAlreadyPresent_DoesNotOverwrite()
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "pre-existing-token");

        await client.SendAsync(request);

        Assert.NotNull(receivedRequest);
        Assert.Equal("Bearer", receivedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("pre-existing-token", receivedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SendAsync_WhenTokenEmpty_LogsWarningAndThrottles()
    {
        var options = new LineOptions { ChannelAccessToken = "" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);
        var logger = new CapturingLogger();
        var innerHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var authHandler = new LineAuthorizationHandler(monitor, logger)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(authHandler);

        using (var req1 = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info"))
        {
            await client.SendAsync(req1);
        }

        Assert.Single(logger.Warnings);
        Assert.Contains("Line:ChannelAccessToken 為空", logger.Warnings[0]);
        Assert.Contains("/edge-admin", logger.Warnings[0]);

        // 第二次呼叫在 10 分鐘內應該被節流
        using (var req2 = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info"))
        {
            await client.SendAsync(req2);
        }

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task SendAsync_WhenAuthorizationAlreadyPresentAndTokenEmpty_DoesNotLogWarning()
    {
        var options = new LineOptions { ChannelAccessToken = "" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);
        var logger = new CapturingLogger();
        var innerHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var authHandler = new LineAuthorizationHandler(monitor, logger)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(authHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "custom-token");

        await client.SendAsync(request);

        Assert.Empty(logger.Warnings);
    }
}
