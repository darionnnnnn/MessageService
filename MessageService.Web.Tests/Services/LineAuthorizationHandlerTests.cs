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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
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

        var authHandler = new LineAuthorizationHandler(monitor, NullLogger<LineAuthorizationHandler>.Instance, TimeProvider.System)
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
    public async Task SendAsync_WhenTokenEmpty_ThrottlesWarningToOncePerTenMinutes()
    {
        var options = new LineOptions { ChannelAccessToken = "" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);
        var logger = new CapturingLogger();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-01T10:00:00Z"));
        var innerHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var authHandler = new LineAuthorizationHandler(monitor, logger, clock)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(authHandler);

        async Task SendOnceAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/info");
            await client.SendAsync(request);
        }

        await SendOnceAsync();
        Assert.Single(logger.Warnings);
        Assert.Contains("Line:ChannelAccessToken 為空", logger.Warnings[0]);
        Assert.Contains("/edge-admin", logger.Warnings[0]);

        // 9 分 59 秒還在節流窗內
        clock.Advance(TimeSpan.FromSeconds(599));
        await SendOnceAsync();
        Assert.Single(logger.Warnings);

        // 跨過 10 分鐘就要再記一次（把節流間隔改長改短，這條都會紅）
        clock.Advance(TimeSpan.FromSeconds(2));
        await SendOnceAsync();
        Assert.Equal(2, logger.Warnings.Count);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public async Task SendAsync_WhenAuthorizationAlreadyPresentAndTokenEmpty_DoesNotLogWarning()
    {
        var options = new LineOptions { ChannelAccessToken = "" };
        var monitor = new FakeOptionsMonitor<LineOptions>(options);
        var logger = new CapturingLogger();
        var innerHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var authHandler = new LineAuthorizationHandler(monitor, logger, TimeProvider.System)
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
