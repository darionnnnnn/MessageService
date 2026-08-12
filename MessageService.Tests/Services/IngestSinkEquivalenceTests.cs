using System.Net;
using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

// Stage 3 最重要的一組測試：拆機版本要跟單機版本「一模一樣」，唯一的防線就是「同一批輸入，
// 兩條落地路徑要產生相同結果」。①DirectIngestSink 直寫本機 DB；②HttpIngestSink 打一個
// 真實啟動的 Db 模式 WebApplicationFactory host（含真實的 IngestController／認證中介層／
// 牠自己的 DirectIngestSink），兩邊各自落地到獨立的資料庫檔，斷言結果完全相同（除了
// 各自資料庫自增的 Id，其餘欄位——含 EventTimestamp／ReceivedAt——全部來自同一個 envelope，
// 沒有任何伺服器端當下時間戳會讓兩邊天生不同）。
public class IngestSinkEquivalenceTests : IDisposable
{
    private readonly string _directDbPath = Path.Combine(Path.GetTempPath(), $"equiv-direct-{Guid.NewGuid():N}.db");
    private readonly string _apiDbPath = Path.Combine(Path.GetTempPath(), $"equiv-api-{Guid.NewGuid():N}.db");
    private const string ApiKey = "equivalence-test-key";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _directDbPath, _apiDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static IngestEnvelope Envelope(string webhookEventId, bool hasContent = false) => new(
        WebhookEventId: webhookEventId,
        LineMessageId: "m-equiv-1",
        GroupId: "Gequiv",
        UserId: "Uequiv",
        MessageType: hasContent ? "image" : "text",
        Text: hasContent ? null : "hello equivalence",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
        ReceivedAt: DateTimeOffset.FromUnixTimeMilliseconds(1700000001000),
        HasContent: hasContent,
        ContentFileName: hasContent ? "photo.jpg" : null);

    private async Task<GroupMessage> SubmitViaDirectAsync(IngestEnvelope envelope)
    {
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite($"Data Source={_directDbPath}").Options;
        using var dbContext = new MessageDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var sink = new DirectIngestSink(dbContext, NullLogger<DirectIngestSink>.Instance);

        await sink.SubmitAsync(envelope, CancellationToken.None);

        return await dbContext.GroupMessages
            .Include(m => m.Content)
            .AsNoTracking()
            .SingleAsync(m => m.WebhookEventId == envelope.WebhookEventId);
    }

    // 不用預設的 Development 環境：那會自動載入這台開發機 user-secrets 裡一把真的 LINE
    // Channel Access Token，讓 Line:OutboundHere 預設 true 卻沒設 ChannelAccessToken 的
    // 啟動驗證規則被意外滿足而測不出問題（只在這台機器「湊巧通過」，CI／乾淨環境會炸）；
    // 同時 appsettings.Development.json 對 Database:Provider 的 Sqlite 覆寫也一併消失，
    // 要明確設定，否則會落回 appsettings.json 的 SqlServer 預設值。
    // 詳見 DeploymentModeTests.CreateFactory 的同款修正說明。
    private WebApplicationFactory<Program> CreateDbModeFactory(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", "Db");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Ingest:ApiKey", ApiKey);
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={dbPath}");
            builder.UseSetting("AllowedClientIps:0", "127.0.0.1");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FakeRemoteIpStartupFilter(IPAddress.Parse("127.0.0.1"))));
        });

    private async Task<GroupMessage> SubmitViaHttpAsync(IngestEnvelope envelope)
    {
        using var factory = CreateDbModeFactory(_apiDbPath);

        var httpClient = factory.CreateClient();
        var sink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = ApiKey }));

        await sink.SubmitAsync(envelope, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.GroupMessages
            .Include(m => m.Content)
            .AsNoTracking()
            .SingleAsync(m => m.WebhookEventId == envelope.WebhookEventId);
    }

    [Fact]
    public async Task TextMessage_DirectAndHttpPaths_ProduceEquivalentRows()
    {
        var envelope = Envelope("evt-equiv-text");

        var direct = await SubmitViaDirectAsync(envelope);
        var viaHttp = await SubmitViaHttpAsync(envelope);

        AssertEquivalent(direct, viaHttp);
        Assert.Null(direct.Content);
        Assert.Null(viaHttp.Content);
    }

    [Fact]
    public async Task MediaMessage_DirectAndHttpPaths_ProduceEquivalentRowsIncludingContent()
    {
        var envelope = Envelope("evt-equiv-media", hasContent: true);

        var direct = await SubmitViaDirectAsync(envelope);
        var viaHttp = await SubmitViaHttpAsync(envelope);

        AssertEquivalent(direct, viaHttp);
        Assert.NotNull(direct.Content);
        Assert.NotNull(viaHttp.Content);
        Assert.Equal(direct.Content!.FileName, viaHttp.Content!.FileName);
        Assert.Equal(direct.Content.DownloadStatus, viaHttp.Content.DownloadStatus);
        Assert.Equal(direct.Content.Content, viaHttp.Content.Content); // 兩邊都還沒下載，皆為 null
        Assert.Equal(direct.Content.CompletedAt, viaHttp.Content.CompletedAt);
    }

    [Fact]
    public async Task DuplicateSubmission_DirectAndHttpPaths_BothReturnSameContentIdWithoutDuplicateRow()
    {
        var envelope = Envelope("evt-equiv-dup", hasContent: true);

        // 直連路徑：驗證重複送兩次不會多一筆，且回傳的 ContentId 一致
        var optionsA = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite($"Data Source={_directDbPath}").Options;
        using (var dbContext = new MessageDbContext(optionsA))
        {
            await dbContext.Database.EnsureCreatedAsync();
            var sink = new DirectIngestSink(dbContext, NullLogger<DirectIngestSink>.Instance);
            var first = await sink.SubmitAsync(envelope, CancellationToken.None);
            var second = await sink.SubmitAsync(envelope, CancellationToken.None);
            Assert.Equal(first.ContentId, second.ContentId);
            Assert.Equal(1, await dbContext.GroupMessages.CountAsync(m => m.WebhookEventId == envelope.WebhookEventId));
        }

        // HTTP 路徑：同一件事透過 ingest API 也要成立——這正是 outbox 重試在拆機情境下
        // 依賴的保證（見 IIngestSink 介面說明「判定為重複時一樣要回傳既有那筆的 ContentId」）
        using var factory = CreateDbModeFactory(_apiDbPath);
        var httpClient = factory.CreateClient();
        var httpSink = new HttpIngestSink(httpClient, OptionsFactory.Create(new IngestOptions { ApiKey = ApiKey }));

        var firstHttp = await httpSink.SubmitAsync(envelope, CancellationToken.None);
        var secondHttp = await httpSink.SubmitAsync(envelope, CancellationToken.None);

        Assert.Equal(firstHttp.ContentId, secondHttp.ContentId);
        using var scope = factory.Services.CreateScope();
        var apiDbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        Assert.Equal(1, await apiDbContext.GroupMessages.CountAsync(m => m.WebhookEventId == envelope.WebhookEventId));
    }

    private static void AssertEquivalent(GroupMessage direct, GroupMessage viaHttp)
    {
        // 刻意不比較 Id／GroupMessageId（各自資料庫的自增值，天生不同）
        Assert.Equal(direct.WebhookEventId, viaHttp.WebhookEventId);
        Assert.Equal(direct.LineMessageId, viaHttp.LineMessageId);
        Assert.Equal(direct.GroupId, viaHttp.GroupId);
        Assert.Equal(direct.UserId, viaHttp.UserId);
        Assert.Equal(direct.MessageType, viaHttp.MessageType);
        Assert.Equal(direct.Text, viaHttp.Text);
        Assert.Equal(direct.StickerId, viaHttp.StickerId);
        Assert.Equal(direct.PackageId, viaHttp.PackageId);
        Assert.Equal(direct.EventTimestamp, viaHttp.EventTimestamp);
        Assert.Equal(direct.ReceivedAt, viaHttp.ReceivedAt);
    }
}
