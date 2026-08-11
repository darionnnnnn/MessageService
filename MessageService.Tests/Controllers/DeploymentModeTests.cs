using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace MessageService.Tests.Controllers;

// 用真實 host 驗證路由閘門與啟動驗證——DeploymentModeConventionTests 只驗 convention
// 本身的行為，這裡驗「controller 從 application model 移除後，請求真的會 404、host
// 起得來」這個依賴 MVC 路由內部行為的最終結果（初版清 Selectors 的做法就是在這裡炸掉的）。
public class DeploymentModeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-mode-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task DbMode_WebhookEndpoint_DoesNotExist()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Ingest:ApiKey", "test-key");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
        });
        using var client = factory.CreateClient();

        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/line/webhook", content);

        // 是 404（路由不存在）而不是 401（存在但簽章不對）——Db 模式下這個端點應該「不存在」
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void LineMode_FailsToStart_BecauseStage2NotImplemented()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Line");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Ingest:BaseUrl", "https://db-host");
            builder.UseSetting("Ingest:ApiKey", "test-key");
        });

        // 啟動驗證應該讓 host 起不來，而不是悄悄跑起來累積永遠排不空的 outbox
        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Deployment:Mode=Line", ex.ToString());
    }
}
