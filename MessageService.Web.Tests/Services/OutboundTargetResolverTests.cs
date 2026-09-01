using System.Net;
using MessageService.Web.Services;

namespace MessageService.Web.Tests.Services;

public class OutboundTargetResolverTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    private sealed class FakeDnsLookup : IDnsLookup
    {
        private readonly Func<string, IPAddress[]> factory;
        public int CallCount { get; private set; }

        public FakeDnsLookup(Func<string, IPAddress[]> factory) => this.factory = factory;

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(factory(host));
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsCommaSeparatedIps_WithIPv4First()
    {
        var dns = new FakeDnsLookup(_ =>
        [
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("203.0.113.10"),
        ]);
        var resolver = new OutboundTargetResolver(new FakeTimeProvider(), dns);

        var result = await resolver.ResolveAsync("api.line.me");

        Assert.Equal("203.0.113.10, 2001:db8::1", result);
    }

    [Fact]
    public async Task ResolveAsync_CachesResultWithinTtl_AndRefetchesAfterExpiry()
    {
        var time = new FakeTimeProvider();
        var dns = new FakeDnsLookup(_ => [IPAddress.Parse("203.0.113.10")]);
        var resolver = new OutboundTargetResolver(time, dns);

        await resolver.ResolveAsync("api.line.me");
        time.Advance(TimeSpan.FromSeconds(59));
        await resolver.ResolveAsync("api.line.me");

        Assert.Equal(1, dns.CallCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await resolver.ResolveAsync("api.line.me");

        Assert.Equal(2, dns.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_LookupThrows_ReturnsNullAndCachesTheFailure()
    {
        var time = new FakeTimeProvider();
        var dns = new FakeDnsLookup(_ => throw new InvalidOperationException("DNS 掛了"));
        var resolver = new OutboundTargetResolver(time, dns);

        Assert.Null(await resolver.ResolveAsync("api.line.me"));

        // 失敗結果同樣快取——重試風暴下不該每次都再等一次逾時
        Assert.Null(await resolver.ResolveAsync("api.line.me"));
        Assert.Equal(1, dns.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_EmptyHost_ReturnsNullWithoutLookup()
    {
        var dns = new FakeDnsLookup(_ => [IPAddress.Parse("203.0.113.10")]);
        var resolver = new OutboundTargetResolver(new FakeTimeProvider(), dns);

        Assert.Null(await resolver.ResolveAsync("  "));
        Assert.Equal(0, dns.CallCount);
    }

    [Theory]
    [InlineData("api.line.me", "203.0.113.10", "api.line.me（IP：203.0.113.10）")]
    [InlineData("api.line.me", null, "api.line.me（IP 解析失敗）")]
    [InlineData("api.line.me", "", "api.line.me（IP 解析失敗）")]
    public void FormatTarget_RendersBothShapes(string host, string? ip, string expected)
    {
        Assert.Equal(expected, OutboundTargetResolver.FormatTarget(host, ip));
    }

    [Fact]
    public async Task ResolveAndFormatAsync_CombinesLookupAndFormatting()
    {
        var dns = new FakeDnsLookup(_ => [IPAddress.Parse("203.0.113.10")]);
        var resolver = new OutboundTargetResolver(new FakeTimeProvider(), dns);

        Assert.Equal(
            "api.line.me（IP：203.0.113.10）",
            await resolver.ResolveAndFormatAsync("api.line.me"));
    }
}
