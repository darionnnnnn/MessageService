using MessageService.Options;
using MessageService.Services;

namespace MessageService.Web.Tests.Services;

public class LineImageUrlRewriterTests
{
    [Fact]
    public void Rewrite_WhenViaIsDirect_ReturnsOriginalUrl()
    {
        var original = "https://profile.line-scdn.net/abc123def";
        var proxy = new Uri("http://192.0.2.10/MSLine/");

        var result = LineImageUrlRewriter.Rewrite(original, LineOutboundVia.Direct, proxy);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Rewrite_WhenProxyBaseAddressIsNull_ReturnsOriginalUrl()
    {
        var original = "https://profile.line-scdn.net/abc123def";

        var result = LineImageUrlRewriter.Rewrite(original, LineOutboundVia.EdgeProxy, null);

        Assert.Equal(original, result);
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("/relative/path/image.jpg")]
    [InlineData("ftp://profile.line-scdn.net/image.jpg")]
    public void Rewrite_WhenOriginalUrlIsNotValidHttpOrHttps_ReturnsOriginalUrl(string invalidUrl)
    {
        var proxy = new Uri("http://192.0.2.10/MSLine/");

        var result = LineImageUrlRewriter.Rewrite(invalidUrl, LineOutboundVia.EdgeProxy, proxy);

        Assert.Equal(invalidUrl, result);
    }

    [Fact]
    public void Rewrite_WhenHostIsNotInAllowedSuffixes_ReturnsOriginalUrl()
    {
        var original = "https://evil.example.com/avatar.jpg?w=100";
        var proxy = new Uri("http://192.0.2.10/MSLine/");

        var result = LineImageUrlRewriter.Rewrite(original, LineOutboundVia.EdgeProxy, proxy);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Rewrite_WhenHostIsAllowed_RewritesToProxyUrlWithQuery()
    {
        var original = "https://profile.line-scdn.net/abc123def/preview?size=large";
        var proxy = new Uri("http://192.0.2.10/MSLine/");

        var result = LineImageUrlRewriter.Rewrite(original, LineOutboundVia.EdgeProxy, proxy);

        Assert.Equal("http://192.0.2.10/MSLine/line/image/profile.line-scdn.net/abc123def/preview?size=large", result);
    }

    [Fact]
    public void Rewrite_WhenHostIsLineMe_RewritesToProxyUrl()
    {
        var original = "https://obs.line.me/myphoto.png";
        var proxy = new Uri("http://192.0.2.10/MSLine");

        var result = LineImageUrlRewriter.Rewrite(original, LineOutboundVia.EdgeProxy, proxy);

        Assert.Equal("http://192.0.2.10/MSLine/line/image/obs.line.me/myphoto.png", result);
    }
}
