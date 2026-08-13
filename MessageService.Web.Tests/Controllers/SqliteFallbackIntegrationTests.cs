using System.Net;
using System.Net.Http.Json;
using MessageService.Data;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Controllers;

// 真實 host 端到端驗證 AllInOne 的 SQLite 救場（需求2）：SQL Server 連不上時整個行程要能正常
// 起來、改用本機 SQLite，而且——這是體檢輪抓到的真 bug——即使 Database:AutoMigrate=false，
// 救場產生的全新 SQLite 檔案也必須跑過 migrate，不然第一筆寫入就會因為缺資料表直接炸掉
// （AutoMigrate=false 的原意是「schema 由外部工具管理」，這個假設對執行期才決定存不存在的
// 救場資料庫不成立）。用真的網路逾時（TEST-NET-3 位址，RFC 5737 保留、保證連不上）而不是
// mock，才能真正驗證到 SqlConnection 逾時→例外→救場觸發這條完整路徑。
public class SqliteFallbackIntegrationTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"messageservice-fallback-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(bool autoMigrate)
    {
        Directory.CreateDirectory(_contentRoot);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(_contentRoot);
            builder.UseSetting("Deployment:Mode", "AllInOne");
            builder.UseSetting("Line:ChannelSecret", "secret");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Heartbeat:Enabled", "false");
            builder.UseSetting("Viewer:AllowedClientIps:0", "127.0.0.1");
            // Database:Provider 刻意不設，讓它依下面的 SqlServer 連線字串推導——這是需求2要驗證
            // 的推導路徑，不是顯式指定
            builder.UseSetting(
                "ConnectionStrings:SqlServer",
                "Server=203.0.113.1,1433;Database=nonexistent;User Id=sa;Password=wrong;" +
                "TrustServerCertificate=True;Connect Timeout=2;");
            builder.UseSetting("Database:AutoMigrate", autoMigrate ? "true" : "false");

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1"))));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreachableSqlServer_TriggersSqliteFallback_RegardlessOfAutoMigrate(bool autoMigrate)
    {
        using var factory = CreateFactory(autoMigrate);
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<DatabaseStatusResponse>("/api/settings/database-status");

        Assert.Equal("Sqlite", status!.EffectiveProvider);
        Assert.True(status.SqliteFallbackActive);
        Assert.NotNull(status.SqliteFallbackReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreachableSqlServer_FallbackDatabase_HasSchemaMigrated_RegardlessOfAutoMigrate(bool autoMigrate)
    {
        // 這是體檢輪抓到的真 bug 的迴歸測試：AutoMigrate=false 時，救場產生的 SQLite 檔案
        // 曾經完全不會跑 migrate，__EFMigrationsHistory／GroupMessages 等資料表都不存在，
        // 第一筆寫入就會直接炸掉
        using var factory = CreateFactory(autoMigrate);
        using var client = factory.CreateClient(); // 觸發真正的 host 啟動（含 migrate）

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        // 能不能真的查（不是「連得上」，是「資料表存在」）才是這個 bug 的關鍵——
        // 缺資料表會在這裡直接丟 SqliteException，而不是回傳空結果
        var canQuery = await Record.ExceptionAsync(() => dbContext.GroupMessages.AsNoTracking().AnyAsync());

        Assert.Null(canQuery);
    }

    private record DatabaseStatusResponse(string EffectiveProvider, bool SqliteFallbackActive, string? SqliteFallbackReason);
}
