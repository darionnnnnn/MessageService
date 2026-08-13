using MessageService.Data;
using MessageService.Models;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

// 這份測試接手了原本 WebhookEventHandlerTests 對資料庫落地行為的斷言（WebhookEventHandler
// 改成只組 envelope 之後，這部分邏輯全部搬進 DirectIngestSink）。
//
// Stage 3：DirectIngestSink 不再持有 IContentDownloadQueue／IProfileRefreshQueue（入列責任
// 移到呼叫端的 IngestSideEffects，理由見 DirectIngestSink 類別註解），這裡改成斷言
// SubmitAsync 的回傳值（IngestResult.ContentId）——入列行為本身的測試搬到 IngestSideEffectsTests。
public class DirectIngestSinkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MessageDbContext _dbContext;
    private readonly DirectIngestSink _sink;

    public DirectIngestSinkTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        _dbContext = new MessageDbContext(options);
        _dbContext.Database.EnsureCreated();
        _sink = new DirectIngestSink(_dbContext, NullLogger<DirectIngestSink>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static IngestEnvelope Envelope(
        string webhookEventId = "evt-1",
        string lineMessageId = "m1",
        string groupId = "G1",
        string? userId = "U1",
        string messageType = "text",
        string? text = "hello",
        string? stickerId = null,
        string? packageId = null,
        bool hasContent = false,
        string? contentFileName = null) =>
        new(webhookEventId, lineMessageId, groupId, userId, messageType, text, stickerId, packageId,
            DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), DateTimeOffset.UtcNow, hasContent, contentFileName);

    [Fact]
    public async Task TextMessage_IsSaved()
    {
        var result = await _sink.SubmitAsync(Envelope(), CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Equal("text", saved.MessageType);
        Assert.Equal("hello", saved.Text);
        Assert.Equal("G1", saved.GroupId);
        Assert.Equal("U1", saved.UserId);
        Assert.Empty(_dbContext.MessageContents);
        Assert.Null(result.ContentId);
    }

    [Fact]
    public async Task TextMessage_TracksGroupLastMessage()
    {
        var result = await _sink.SubmitAsync(Envelope(), CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        var group = await _dbContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Equal(saved.Id, group.LastMessageId);
        Assert.Equal(saved.EventTimestamp, group.LastMessageAt);
        Assert.Null(group.GroupName); // 頭貼快取的職責，這裡只補 stub
    }

    [Fact]
    public async Task SecondMessageInSameGroup_AdvancesLastMessageId_DoesNotCreateSecondGroupRow()
    {
        await _sink.SubmitAsync(Envelope(webhookEventId: "evt-1"), CancellationToken.None);
        var second = await _sink.SubmitAsync(Envelope(webhookEventId: "evt-2", lineMessageId: "m2"), CancellationToken.None);

        var groups = await _dbContext.Groups.AsNoTracking().Where(g => g.GroupId == "G1").ToListAsync();
        var group = Assert.Single(groups);
        var secondMessage = await _dbContext.GroupMessages.AsNoTracking().SingleAsync(m => m.WebhookEventId == "evt-2");
        Assert.Equal(secondMessage.Id, group.LastMessageId);
    }

    [Fact]
    public async Task MessageInGroupWithExistingCachedProfile_PreservesGroupNameAndPictureUrl()
    {
        // 頭貼快取（DbProfileStore）已經抓過這個群組的名稱/頭貼——訊息落地時的 Groups 追蹤
        // 只該更新 LastMessageId/At，不該把已有的 GroupName/PictureUrl 覆蓋掉
        _dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "工作群組", PictureUrl = "https://x/p.png", UpdatedAt = DateTimeOffset.UtcNow });
        await _dbContext.SaveChangesAsync();

        await _sink.SubmitAsync(Envelope(), CancellationToken.None);

        var group = await _dbContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Equal("工作群組", group.GroupName);
        Assert.Equal("https://x/p.png", group.PictureUrl);
    }

    [Fact]
    public async Task StickerMessage_SavesStickerIdAndPackageId()
    {
        await _sink.SubmitAsync(
            Envelope(messageType: "sticker", text: "(貼圖)", stickerId: "52002734", packageId: "11537"),
            CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Equal("52002734", saved.StickerId);
        Assert.Equal("11537", saved.PackageId);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ImageOrVideoOrAudioMessage_CreatesPendingContentAndReturnsContentId(string messageType)
    {
        var result = await _sink.SubmitAsync(Envelope(messageType: messageType, text: null, hasContent: true), CancellationToken.None);

        var savedMessage = Assert.Single(_dbContext.GroupMessages);
        Assert.Null(savedMessage.Text);
        var content = Assert.Single(_dbContext.MessageContents);
        Assert.Equal(DownloadStatus.Pending, content.DownloadStatus);
        Assert.Equal(savedMessage.Id, content.GroupMessageId);
        Assert.Equal(content.Id, result.ContentId);
    }

    [Fact]
    public async Task FileMessage_StoresFileNameAndReturnsContentId()
    {
        var result = await _sink.SubmitAsync(
            Envelope(messageType: "file", text: null, hasContent: true, contentFileName: "report.pdf"),
            CancellationToken.None);

        var content = Assert.Single(_dbContext.MessageContents);
        Assert.Equal("report.pdf", content.FileName);
        Assert.Equal(DownloadStatus.Pending, content.DownloadStatus);
        Assert.Equal(content.Id, result.ContentId);
    }

    [Fact]
    public async Task DuplicateWebhookEventId_IsSkipped()
    {
        // 這裡驗證的正是防重送真正的保證來源：GroupMessages.WebhookEventId 的唯一索引
        // （MessageDbContext）＋這裡攔的 DbUpdateException，不是任何預查
        await _sink.SubmitAsync(Envelope(webhookEventId: "evt-1"), CancellationToken.None);
        await _sink.SubmitAsync(Envelope(webhookEventId: "evt-1"), CancellationToken.None);

        Assert.Single(_dbContext.GroupMessages);
    }

    [Fact]
    public async Task DuplicateWebhookEventId_TextMessage_ReturnsNullContentId()
    {
        await _sink.SubmitAsync(Envelope(webhookEventId: "evt-1"), CancellationToken.None);
        var result = await _sink.SubmitAsync(Envelope(webhookEventId: "evt-1"), CancellationToken.None);

        Assert.Null(result.ContentId);
    }

    [Fact]
    public async Task DuplicateWebhookEventId_MediaMessage_ReturnsExistingContentId()
    {
        // 這是 Stage 3 加 ContentId 回傳之後才有意義的情境：outbox 重試（代表前一次的回應
        // 可能遺失了）若在這裡回 null，拆機模式的那筆媒體就要等到下次服務重啟才會被撿回
        var first = await _sink.SubmitAsync(
            Envelope(webhookEventId: "evt-dup-media", messageType: "image", text: null, hasContent: true),
            CancellationToken.None);
        var second = await _sink.SubmitAsync(
            Envelope(webhookEventId: "evt-dup-media", messageType: "image", text: null, hasContent: true),
            CancellationToken.None);

        Assert.NotNull(first.ContentId);
        Assert.Equal(first.ContentId, second.ContentId);
        Assert.Single(_dbContext.MessageContents); // 沒有因為第二次呼叫多插一筆
    }

    // ==== DbUpdateException 的雙面性：撞鍵（重複）要當成功、暫時性失敗要往外拋讓 outbox 重試 ====
    // 這組測試用帶 SaveChangesInterceptor 的獨立 context，不共用建構子裡的 _sink

    private (DirectIngestSink sink, MessageDbContext dbContext, SaveFailureInterceptor interceptor) CreateSinkWithInterceptor()
    {
        var interceptor = new SaveFailureInterceptor();
        var options = new DbContextOptionsBuilder<MessageDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        var dbContext = new MessageDbContext(options);
        var sink = new DirectIngestSink(dbContext, NullLogger<DirectIngestSink>.Instance);
        return (sink, dbContext, interceptor);
    }

    [Fact]
    public async Task TransientSaveFailure_Throws_SoOutboxRetriesInsteadOfDroppingMessage()
    {
        // outbox 的核心承諾是「DB 短暫不可用不掉訊息」：暫時性儲存失敗若被當成重複吞掉，
        // outbox 會把該筆刪掉、訊息就真的掉了——必須往外拋讓 forwarder 排程重試
        var (sink, dbContext, interceptor) = CreateSinkWithInterceptor();
        using var _ = dbContext;
        interceptor.ThrowOnce = true;

        await Assert.ThrowsAsync<DbUpdateException>(
            () => sink.SubmitAsync(Envelope(webhookEventId: "evt-transient"), CancellationToken.None));

        Assert.False(await dbContext.GroupMessages.AnyAsync(m => m.WebhookEventId == "evt-transient"));
    }

    [Fact]
    public async Task TransientSaveFailure_DoesNotPoisonLaterSubmitsOnSameContext()
    {
        // forwarder 一個批次共用同一個 scope 的 DbContext：失敗實體若留在 change tracker
        // （Added 狀態），同批下一筆的 SaveChanges 會連它一起再插一次
        var (sink, dbContext, interceptor) = CreateSinkWithInterceptor();
        using var _ = dbContext;
        interceptor.ThrowOnce = true;
        await Assert.ThrowsAsync<DbUpdateException>(
            () => sink.SubmitAsync(Envelope(webhookEventId: "evt-failed"), CancellationToken.None));

        await sink.SubmitAsync(Envelope(webhookEventId: "evt-next"), CancellationToken.None);

        var saved = Assert.Single(await dbContext.GroupMessages.AsNoTracking().ToListAsync());
        Assert.Equal("evt-next", saved.WebhookEventId);
    }

    [Fact]
    public async Task UniqueConstraintViolationDuringSave_IsTreatedAsDuplicate_NotRethrown()
    {
        // 走過預查、儲存時才撞上唯一索引（例如 outbox 重試時前一次其實已寫入）：
        // 回查發現資料在，要當重複成功處理，不能拋出去讓 outbox 無限重試同一筆
        var (sink, dbContext, interceptor) = CreateSinkWithInterceptor();
        using var _ = dbContext;
        interceptor.BeforeSaveOnce = async () =>
        {
            // 在 SaveChanges 執行前，用另一個 context 先把同一個 WebhookEventId 寫進去
            var rivalOptions = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
            using var rival = new MessageDbContext(rivalOptions);
            rival.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "evt-race",
                LineMessageId = "m-rival",
                GroupId = "G1",
                MessageType = "text",
                Text = "first writer wins",
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await rival.SaveChangesAsync();
        };

        // 不應該拋——回查會發現資料已存在
        var result = await sink.SubmitAsync(Envelope(webhookEventId: "evt-race"), CancellationToken.None);

        var saved = Assert.Single(await dbContext.GroupMessages.AsNoTracking().ToListAsync());
        Assert.Equal("m-rival", saved.LineMessageId);
        Assert.Null(result.ContentId); // rival 是文字訊息，沒有媒體內容
    }

    [Fact]
    public async Task UniqueConstraintViolationDuringSave_RivalHasContent_ReturnsRivalContentId()
    {
        // 同上，但 rival 這次帶媒體內容——驗證回查那段投影（new { m.Id, ContentId = ... }）
        // 真的把既有內容的 Id 撈出來，不是永遠回 null
        var (sink, dbContext, interceptor) = CreateSinkWithInterceptor();
        using var _ = dbContext;
        long rivalContentId = 0;
        interceptor.BeforeSaveOnce = async () =>
        {
            var rivalOptions = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
            using var rival = new MessageDbContext(rivalOptions);
            var rivalMessage = new GroupMessage
            {
                WebhookEventId = "evt-race-media",
                LineMessageId = "m-rival-media",
                GroupId = "G1",
                MessageType = "image",
                Content = new MessageContent { DownloadStatus = DownloadStatus.Pending },
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow
            };
            rival.GroupMessages.Add(rivalMessage);
            await rival.SaveChangesAsync();
            rivalContentId = rivalMessage.Content.Id;
        };

        var result = await sink.SubmitAsync(
            Envelope(webhookEventId: "evt-race-media", messageType: "image", text: null, hasContent: true),
            CancellationToken.None);

        Assert.Equal(rivalContentId, result.ContentId);
    }
}
