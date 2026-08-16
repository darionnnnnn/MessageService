using MessageService.Data;
using MessageService.Models;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

public class StickerContentBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public StickerContentBackfillServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_BackfillsMissingContentRows_EnqueuesNewContentIds_AndIsIdempotent()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var msg1 = new GroupMessage { WebhookEventId = "evt1", LineMessageId = "msg1", GroupId = "g1", MessageType = "sticker", StickerId = "123", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var msg2 = new GroupMessage { WebhookEventId = "evt2", LineMessageId = "msg2", GroupId = "g1", MessageType = "text", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var msg3 = new GroupMessage { WebhookEventId = "evt3", LineMessageId = "msg3", GroupId = "g1", MessageType = "sticker", StickerId = null, EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var msg4 = new GroupMessage { WebhookEventId = "evt4", LineMessageId = "msg4", GroupId = "g1", MessageType = "sticker", StickerId = "124", Content = new MessageContent { DownloadStatus = DownloadStatus.Completed }, EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var msg5 = new GroupMessage { WebhookEventId = "evt5", LineMessageId = "msg5", GroupId = "g1", MessageType = "sticker", StickerId = "125", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };

        db.GroupMessages.AddRange(msg1, msg2, msg3, msg4, msg5);
        await db.SaveChangesAsync();

        var queue = new FakeContentDownloadQueue();
        var service = new StickerContentBackfillService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            NullLogger<StickerContentBackfillService>.Instance);

        await service.RunBackfillAsync(CancellationToken.None);

        // msg1 與 msg5 應該補上 pending content
        var reloadedMsg1 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg1");
        Assert.NotNull(reloadedMsg1.Content);
        Assert.Equal(DownloadStatus.Pending, reloadedMsg1.Content.DownloadStatus);

        var reloadedMsg5 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg5");
        Assert.NotNull(reloadedMsg5.Content);
        Assert.Equal(DownloadStatus.Pending, reloadedMsg5.Content.DownloadStatus);

        // 其他訊息不受影響
        var reloadedMsg2 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg2");
        Assert.Null(reloadedMsg2.Content);

        var reloadedMsg3 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg3");
        Assert.Null(reloadedMsg3.Content);

        var reloadedMsg4 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg4");
        Assert.NotNull(reloadedMsg4.Content);
        Assert.Equal(DownloadStatus.Completed, reloadedMsg4.Content.DownloadStatus);

        // 驗證假佇列收到的 Id 集合等於新建內容列的 Id 集合
        var expectedContentIds = new[] { reloadedMsg1.Content.Id, reloadedMsg5.Content.Id };
        Assert.Equal(expectedContentIds, queue.Enqueued);

        // 重複執行一次（驗證冪等性），不會再入列任何 Id
        await service.RunBackfillAsync(CancellationToken.None);

        Assert.Equal(expectedContentIds, queue.Enqueued);

        var contentCount = await db.MessageContents.CountAsync();
        Assert.Equal(3, contentCount); // msg1, msg4, msg5's contents
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoPendingItems_ShortCircuitsWithoutEnqueuing()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        // 資料庫中只有文字訊息或已有內容列的貼圖訊息
        var msg1 = new GroupMessage { WebhookEventId = "evt1", LineMessageId = "msg1", GroupId = "g1", MessageType = "text", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };
        var msg2 = new GroupMessage { WebhookEventId = "evt2", LineMessageId = "msg2", GroupId = "g1", MessageType = "sticker", StickerId = "124", Content = new MessageContent { DownloadStatus = DownloadStatus.Completed }, EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow };

        db.GroupMessages.AddRange(msg1, msg2);
        await db.SaveChangesAsync();

        var queue = new FakeContentDownloadQueue();
        var service = new StickerContentBackfillService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            NullLogger<StickerContentBackfillService>.Instance);

        await service.RunBackfillAsync(CancellationToken.None);

        // 不應入列任何 Id
        Assert.Empty(queue.Enqueued);

        // 內容列數量保持不變
        var contentCount = await db.MessageContents.CountAsync();
        Assert.Equal(1, contentCount);
    }
}
