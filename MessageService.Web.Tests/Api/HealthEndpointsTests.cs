using System.Net;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class HealthEndpointsTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetHealthz_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthz_WhenClientIpNotAllowed_BypassesAllowlistAndReturnsOk()
    {
        // 設定不包含測試端來源 IP（127.0.0.1）的白名單清單，模擬監控系統或外部探針連線
        using var restrictedFixture = new WebAppFactoryFixture(allowedClientIps: ["10.0.0.1"]);

        // 先確認一般檢視端 API 確實會被 IP 白名單阻擋（回傳 403 Forbidden）
        var blockedResponse = await restrictedFixture.Client.GetAsync("/api/groups");
        Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);

        // 驗證 /healthz 存活探針已正確自白名單中排除，仍能成功回應 200 OK
        var healthResponse = await restrictedFixture.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
    }

    [Fact]
    public async Task GetHealthzReady_WhenClientIpNotAllowed_BypassesAllowlistAndReturnsOk()
    {
        // 設定不包含測試端來源 IP（127.0.0.1）的白名單清單，模擬監控系統或外部探針連線
        using var restrictedFixture = new WebAppFactoryFixture(allowedClientIps: ["10.0.0.1"]);

        // 先確認一般檢視端 API 確實會被 IP 白名單阻擋（回傳 403 Forbidden）
        var blockedResponse = await restrictedFixture.Client.GetAsync("/api/groups");
        Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);

        // 驗證 /healthz/ready 就緒探針在資料庫可連線時回傳 200 OK，且不受 IP 白名單限制
        var readyResponse = await restrictedFixture.Client.GetAsync("/healthz/ready");
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
    }

    [Fact]
    public async Task GetHealthz_ResponseBody_IsEmpty()
    {
        var response = await _fixture.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Empty(body);
    }

    [Fact]
    public async Task GetHealthzReady_ResponseBody_IsEmpty()
    {
        var response = await _fixture.Client.GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Empty(body);
    }
}
