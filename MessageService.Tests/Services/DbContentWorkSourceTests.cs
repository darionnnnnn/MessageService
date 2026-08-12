using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

// GetPendingIdsAsync 的 Failed 重試視窗（FailedRetryWindowDays／MaxFailedRetries）與
// CompleteAsync 的串流寫入（SQLite：zeroblob + SqliteBlob）——這兩塊是批次 C 新增的邏輯，
// 跟既有的 ContentDownloadServiceTests（走 ProcessAsync 整條流程）互補，這裡直接測
// DbContentWorkSource 本身，涵蓋不透過 ContentDownloadService 就看不到的邊界情況。
public class DbContentWorkSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public DbContentWorkSourceTests()
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

    private DbContentWorkSource CreateSource(MessageDbContext dbContext, ContentDownloadOptions? options = null) =>
        new(dbContext, OptionsFactory.Create(options ?? new ContentDownloadOptions()));

    // 用全新的 scope／DbContext 重新查詢——CompleteAsync 對 blob 欄位是繞過 EF change tracker
    // 直接下 raw ADO 指令寫入的（見類別說明），沿用同一個 DbContext 讀回來會拿到查詢前就已經
    // 追蹤、沒被更新過的舊實體（EF 的 identity map 不會用查詢結果覆寫已追蹤實體的屬性）
    private async Task<MessageContent> ReloadContentAsync(long id)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.MessageContents.SingleAsync(c => c.Id == id);
    }

    private async Task<(MessageDbContext DbContext, long ContentId)> SeedFailedContentAsync(
        DateTimeOffset receivedAt, int failedAttempts = 1)
    {
        var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(),
            LineMessageId = "line-msg-1",
            GroupId = "G1",
            MessageType = "image",
            EventTimestamp = receivedAt,
            ReceivedAt = receivedAt,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Failed, FailedAttempts = failedAttempts }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        return (dbContext, groupMessage.Content!.Id);
    }

    [Fact]
    public async Task GetPendingIdsAsync_FailedWithinWindowAndBelowMaxRetries_IsRequeued()
    {
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow.AddDays(-1), failedAttempts: 1);
        var source = CreateSource(dbContext, new ContentDownloadOptions { FailedRetryWindowDays = 7, MaxFailedRetries = 10 });

        var ids = await source.GetPendingIdsAsync(CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(ids));
        var reloaded = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_FailedOutsideRetryWindow_IsNotRequeued()
    {
        // 訊息到達已經超過保留視窗（LINE 內容過期，重試也下載不到）
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow.AddDays(-10), failedAttempts: 1);
        var source = CreateSource(dbContext, new ContentDownloadOptions { FailedRetryWindowDays = 7, MaxFailedRetries = 10 });

        var ids = await source.GetPendingIdsAsync(CancellationToken.None);

        Assert.Empty(ids);
        var reloaded = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_FailedAttemptsAtOrAboveMax_IsNotRequeued()
    {
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow.AddDays(-1), failedAttempts: 10);
        var source = CreateSource(dbContext, new ContentDownloadOptions { FailedRetryWindowDays = 7, MaxFailedRetries = 10 });

        var ids = await source.GetPendingIdsAsync(CancellationToken.None);

        Assert.Empty(ids);
        var reloaded = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_IncludesPlainPendingContent_RegardlessOfWindow()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var ids = await source.GetPendingIdsAsync(CancellationToken.None);

        Assert.Equal(groupMessage.Content!.Id, Assert.Single(ids));
    }

    [Fact]
    public async Task FailAsync_IncrementsFailedAttemptsAndSetsLastAttemptAt()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending, FailedAttempts = 2 }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var before = DateTimeOffset.UtcNow;
        await source.FailAsync(groupMessage.Content!.Id, CancellationToken.None);

        var reloaded = await dbContext.MessageContents.SingleAsync(c => c.Id == groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
        Assert.Equal(3, reloaded.FailedAttempts);
        Assert.NotNull(reloaded.LastAttemptAt);
        Assert.True(reloaded.LastAttemptAt >= before);
    }

    [Fact]
    public async Task CompleteAsync_StreamsContentIntoBlobColumn_AndSetsMetadata()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var payload = new byte[] { 10, 20, 30, 40, 50 };
        await source.CompleteAsync(groupMessage.Content!.Id, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None);

        var reloaded = await ReloadContentAsync(groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Completed, reloaded.DownloadStatus);
        Assert.Equal(payload, reloaded.Content);
        Assert.Equal("image/png", reloaded.ContentType);
        Assert.NotNull(reloaded.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_LargePayload_RoundTripsExactly()
    {
        // 跨過 SqliteBlob 內部緩衝區大小的邊界（BufferSize=81920），驗證分塊寫入不會漏位元組
        // 或錯位，比 CompleteAsync_StreamsContentIntoBlobColumn 那組小 payload 更貼近真實檔案
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "video",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var payload = new byte[200_000];
        new Random(42).NextBytes(payload);

        await source.CompleteAsync(groupMessage.Content!.Id, new MemoryStream(payload), payload.Length, "video/mp4", CancellationToken.None);

        var reloaded = await ReloadContentAsync(groupMessage.Content.Id);
        Assert.Equal(payload, reloaded.Content);
    }
}
