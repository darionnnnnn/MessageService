using MessageService.Services;

namespace MessageService.Tests.Services;

public class HttpBaseAddressTests
{
    [Fact]
    public void PlainUri_WithoutTrailingSlash_LosesTheSubApplicationPath()
    {
        // 這是實測踩到的 404 根因：IIS 子應用程式底下的站台（http://host/MSLine），
        // BaseAddress 少了結尾斜線時 RFC 3986 會把最後一段路徑換掉，子應用路徑整段消失
        var resolved = new Uri(new Uri("http://10.231.145.94/MSLine"), "api/edge/poll");

        Assert.Equal("http://10.231.145.94/api/edge/poll", resolved.ToString());
    }

    [Theory]
    [InlineData("http://10.231.145.94/MSLine")]
    [InlineData("http://10.231.145.94/MSLine/")]
    [InlineData("  http://10.231.145.94/MSLine  ")]
    public void Create_KeepsSubApplicationPath_RegardlessOfTrailingSlash(string configured)
    {
        var resolved = new Uri(HttpBaseAddress.Create(configured), "api/edge/poll");

        Assert.Equal("http://10.231.145.94/MSLine/api/edge/poll", resolved.ToString());
    }

    [Theory]
    [InlineData("https://core-host.example")]
    [InlineData("https://core-host.example/")]
    public void Create_RootSitesAreUnaffected(string configured)
    {
        var resolved = new Uri(HttpBaseAddress.Create(configured), "api/ingest/events");

        Assert.Equal("https://core-host.example/api/ingest/events", resolved.ToString());
    }

    [Fact]
    public void Create_NestedPath_KeepsEverySegment()
    {
        var resolved = new Uri(HttpBaseAddress.Create("http://host/apps/line/edge"), "api/edge/poll");

        Assert.Equal("http://host/apps/line/edge/api/edge/poll", resolved.ToString());
    }

    [Fact]
    public void Create_InvalidUri_Throws()
    {
        Assert.Throws<UriFormatException>(() => HttpBaseAddress.Create("not-a-url"));
    }
}
