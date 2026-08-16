using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
using MessageService.Web.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task RunBackfillAsync_WhenBatchThrowsDbUpdateException_ClearsChangeTracker_AdvancesCursor_AndContinuesToNextBatch()
    {
        using var connection = SqliteTestDatabase.CreateOpenConnection();
        var interceptor = new FirstBatchDbUpdateExceptionInterceptor();
        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o =>
            o.UseSqlite(connection)
             .AddInterceptors(interceptor));
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            db.Database.EnsureCreated();

            // 建立 501 筆貼圖訊息：前 500 筆為第 1 批，第 501 筆為第 2 批
            var messages = new List<GroupMessage>(501);
            for (int i = 1; i <= 501; i++)
            {
                messages.Add(new GroupMessage
                {
                    WebhookEventId = $"evt_{i}",
                    LineMessageId = $"msg_{i}",
                    GroupId = "g1",
                    MessageType = "sticker",
                    StickerId = $"sticker_{i}",
                    EventTimestamp = DateTimeOffset.UtcNow,
                    ReceivedAt = DateTimeOffset.UtcNow
                });
            }
            db.GroupMessages.AddRange(messages);
            await db.SaveChangesAsync();
        }

        interceptor.Enabled = true;

        var queue = new FakeContentDownloadQueue();
        var service = new StickerContentBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            NullLogger<StickerContentBackfillService>.Instance);

        await service.RunBackfillAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

            // 第 1 批因撞鍵失敗，該批未成功寫入 Content
            var reloadedMsg1 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_1");
            Assert.Null(reloadedMsg1.Content);

            // 第 2 批成功執行，msg_501 成功取得 MessageContent
            var reloadedMsg501 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_501");
            Assert.NotNull(reloadedMsg501.Content);
            Assert.Equal(DownloadStatus.Pending, reloadedMsg501.Content.DownloadStatus);

            // 只有第 2 批成功的 Content Id 會被丟進下載佇列
            Assert.Single(queue.Enqueued);
            Assert.Equal(reloadedMsg501.Content.Id, queue.Enqueued.Single());
        }
    }

    [Theory]
    [InlineData(DeploymentMode.Viewer, false)]
    [InlineData(DeploymentMode.Core, true)]
    [InlineData(DeploymentMode.AllInOne, true)]
    [InlineData(DeploymentMode.Edge, false)]
    public void AddMessageServiceCore_RegistersStickerContentBackfillService_AccordingToDeploymentMode(
        DeploymentMode mode, bool shouldBeRegistered)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:Sqlite"] = "Data Source=:memory:";
        var ingestOptions = new IngestOptions { BaseUrl = "https://example.com" };
        var capabilities = DeploymentCapabilities.Derive(mode, new LineOptions(), new ViewerOptions(), ingestOptions);

        builder.AddMessageServiceCore(capabilities, mode, ingestOptions);

        var isRegistered = builder.Services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(StickerContentBackfillService));

        Assert.Equal(shouldBeRegistered, isRegistered);
    }

    private sealed class FirstBatchDbUpdateExceptionInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int _callCount;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && Interlocked.Increment(ref _callCount) == 1)
            {
                throw new DbUpdateException(
                    "Simulated duplicate key collision on batch 1",
                    new Exception("UNIQUE constraint failed: MessageContents.GroupMessageId"));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
