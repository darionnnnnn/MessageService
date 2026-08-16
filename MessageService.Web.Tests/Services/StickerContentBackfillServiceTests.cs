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
using Microsoft.Extensions.Logging;
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

            // 第 1 批批次存檔撞鍵，逐筆重試後該批全部 500 筆成功寫入 Content
            var reloadedMsg1 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_1");
            Assert.NotNull(reloadedMsg1.Content);
            Assert.Equal(DownloadStatus.Pending, reloadedMsg1.Content.DownloadStatus);

            // 第 2 批成功執行，msg_501 成功取得 MessageContent
            var reloadedMsg501 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_501");
            Assert.NotNull(reloadedMsg501.Content);
            Assert.Equal(DownloadStatus.Pending, reloadedMsg501.Content.DownloadStatus);

            // 第 1 批重試成功（500 筆）與第 2 批（1 筆）的 Content Id 皆被丟進下載佇列（共 501 筆）
            Assert.Equal(501, queue.Enqueued.Count);
            Assert.Contains(reloadedMsg1.Content.Id, queue.Enqueued);
            Assert.Contains(reloadedMsg501.Content.Id, queue.Enqueued);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_WhenSingleMessageInBatchCollides_RemainingMessagesInBatchAreBackfilledAndEnqueued()
    {
        using var connection = SqliteTestDatabase.CreateOpenConnection();
        var interceptor = new SelectiveCollisionDbUpdateExceptionInterceptor();
        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o =>
            o.UseSqlite(connection)
             .AddInterceptors(interceptor));
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            db.Database.EnsureCreated();

            db.GroupMessages.AddRange(
                new GroupMessage { WebhookEventId = "evt_1", LineMessageId = "msg_1", GroupId = "g1", MessageType = "sticker", StickerId = "s1", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow },
                new GroupMessage { WebhookEventId = "evt_2", LineMessageId = "msg_2", GroupId = "g1", MessageType = "sticker", StickerId = "s2", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow },
                new GroupMessage { WebhookEventId = "evt_3", LineMessageId = "msg_3", GroupId = "g1", MessageType = "sticker", StickerId = "s3", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow }
            );
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

            // msg_1 與 msg_3 應成功補上 Content
            var reloadedMsg1 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_1");
            Assert.NotNull(reloadedMsg1.Content);
            Assert.Equal(DownloadStatus.Pending, reloadedMsg1.Content.DownloadStatus);

            var reloadedMsg3 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_3");
            Assert.NotNull(reloadedMsg3.Content);
            Assert.Equal(DownloadStatus.Pending, reloadedMsg3.Content.DownloadStatus);

            // msg_2 撞唯一鍵，未補上 Content
            var reloadedMsg2 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_2");
            Assert.Null(reloadedMsg2.Content);

            // 下載佇列中只應有 msg_1 與 msg_3 的 Content Id
            Assert.Equal(2, queue.Enqueued.Count);
            Assert.Equal(new[] { reloadedMsg1.Content.Id, reloadedMsg3.Content.Id }, queue.Enqueued);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_WhenBatchThrowsNonUniqueDbUpdateException_PropagatesToOuterErrorHandlerAndDoesNotEnqueue()
    {
        using var connection = SqliteTestDatabase.CreateOpenConnection();
        var interceptor = new NonUniqueDbUpdateExceptionInterceptor();
        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o =>
            o.UseSqlite(connection)
             .AddInterceptors(interceptor));
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            db.Database.EnsureCreated();

            db.GroupMessages.AddRange(
                new GroupMessage { WebhookEventId = "evt_1", LineMessageId = "msg_1", GroupId = "g1", MessageType = "sticker", StickerId = "s1", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow },
                new GroupMessage { WebhookEventId = "evt_2", LineMessageId = "msg_2", GroupId = "g1", MessageType = "sticker", StickerId = "s2", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow }
            );
            await db.SaveChangesAsync();
        }

        interceptor.Enabled = true;

        var queue = new FakeContentDownloadQueue();
        var logger = new CapturingLogger<StickerContentBackfillService>();
        var service = new StickerContentBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            logger);

        await service.RunBackfillAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

            // 非唯一鍵衝突時不應被當成「別處已補好」靜靜略過，訊息 Content 仍為 null
            var reloadedMsg1 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_1");
            Assert.Null(reloadedMsg1.Content);

            var reloadedMsg2 = await db.GroupMessages.Include(m => m.Content).SingleAsync(m => m.LineMessageId == "msg_2");
            Assert.Null(reloadedMsg2.Content);

            // 不應入列任何下載項目
            Assert.Empty(queue.Enqueued);

            // 應走到最外層的 LogError
            Assert.Contains(logger.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("Failed to backfill sticker content rows."));
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
                    new SqliteException("UNIQUE constraint failed: MessageContents.GroupMessageId", 19, 2067));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SelectiveCollisionDbUpdateExceptionInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int _callCount;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                var count = Interlocked.Increment(ref _callCount);
                // 第 1 次為批次存檔（3 筆），丟出唯一鍵衝突
                // 第 3 次為第 2 筆訊息逐筆存檔時，丟出唯一鍵衝突
                if (count is 1 or 3)
                {
                    throw new DbUpdateException(
                        "Simulated duplicate key collision",
                        new SqliteException("UNIQUE constraint failed: MessageContents.GroupMessageId", 19, 2067));
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class NonUniqueDbUpdateExceptionInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                // 丟出非唯一鍵衝突的例外（如 SQLite_BUSY 錯誤碼 5）
                throw new DbUpdateException(
                    "Simulated transient locked error",
                    new SqliteException("database is locked", 5, 5));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
