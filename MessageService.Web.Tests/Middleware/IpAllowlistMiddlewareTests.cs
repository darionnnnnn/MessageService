using System.Net;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Middleware;

public class IpAllowlistMiddlewareTests
{
    [Fact]
    public async Task Request_FromAllowedIp_Succeeds()
    {
        using var fixture = new WebAppFactoryFixture(["127.0.0.1", "::1"]);

        var response = await fixture.Client.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_FromNonAllowedIp_Returns403()
    {
        using var fixture = new WebAppFactoryFixture(["10.0.0.1"]);

        var response = await fixture.Client.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithEmptyAllowlist_Returns403()
    {
        using var fixture = new WebAppFactoryFixture([]);

        var response = await fixture.Client.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_FromIpInsideAllowedCidrRange_Succeeds()
    {
        // TestServer 的 RemoteIpAddress 固定是 127.0.0.1，用涵蓋它的網段驗證 CIDR 解析有生效
        using var fixture = new WebAppFactoryFixture(["127.0.0.0/8"]);

        var response = await fixture.Client.GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
