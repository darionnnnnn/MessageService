using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    private DbContentWorkSource CreateSource(MessageDbContext dbContext, ContentDownloadOptions? options = null, FieldCipher? cipher = null) =>
        new(dbContext, OptionsFactory.Create(options ?? new ContentDownloadOptions()), cipher ?? FieldCipher.Disabled);

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

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(ids));
        // AsNoTracking 是必要的：狀態是用 ExecuteUpdateAsync 直接下 SQL 改的（刻意不載入實體，
        // 否則會把 Failed 列的整顆 blob 一起撈進記憶體），這種批次更新不會同步既有的 change
        // tracker 快照，追蹤查詢會回傳過期的 Failed。正式流程不受影響——RequeuePendingAsync
        // 用完就把 scope 丟掉，後續的 ProcessAsync 各自開新的 scope 與新的 DbContext。
        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_FailedOutsideRetryWindow_IsNotRequeued()
    {
        // 訊息到達已經超過保留視窗（LINE 內容過期，重試也下載不到）
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow.AddDays(-10), failedAttempts: 1);
        var source = CreateSource(dbContext, new ContentDownloadOptions { FailedRetryWindowDays = 7, MaxFailedRetries = 10 });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Empty(ids);
        var reloaded = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_FailedAttemptsAtOrAboveMax_IsNotRequeued()
    {
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow.AddDays(-1), failedAttempts: 10);
        var source = CreateSource(dbContext, new ContentDownloadOptions { FailedRetryWindowDays = 7, MaxFailedRetries = 10 });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

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

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal(groupMessage.Content!.Id, Assert.Single(ids));
    }

    // GetAsync 不改狀態、可以安全重複呼叫——影片／語音要靠它反覆查詢轉檔狀態（見
    // ContentDownloadService.CheckTranscodingAsync），同一個 worker 對同一筆內容會多次呼叫；
    // 真正的認領動作在 CompleteAsync（見該方法說明與下面「認領機制」那組測試）
    [Fact]
    public async Task GetAsync_PendingContent_ReturnsWorkItem_WithoutChangingStatus()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "line-m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var first = await source.GetAsync(groupMessage.Content!.Id, CancellationToken.None);
        var second = await source.GetAsync(groupMessage.Content.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("line-m1", first!.LineMessageId);
        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetAsync_DownloadingContent_ReturnsNull()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "line-m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Downloading }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var item = await source.GetAsync(groupMessage.Content!.Id, CancellationToken.None);

        Assert.Null(item);
    }

    // === CompleteAsync 的認領機制：多個 worker 共讀同一個 Channel，同一個 contentId 有機會
    // 被入列兩次——CompleteAsync 開頭用一句 ExecuteUpdateAsync 認領（Pending→Downloading），
    // 沒認領到的那個直接跳過，避免兩邊同時對同一顆 blob 交錯寫入，見該方法說明 ===

    [Fact]
    public async Task CompleteAsync_SecondCallAfterAlreadyCompleted_DoesNotOverwriteContent()
    {
        // 模擬兩個 worker 都下載完同一筆內容、依序呼叫 CompleteAsync——第一個成功寫入並標
        // Completed，第二個的認領會因為狀態已經不是 Pending 而拿到 0，必須直接跳過，
        // 不能覆寫第一個已經寫好的內容
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
        var contentId = groupMessage.Content!.Id;

        var firstPayload = new byte[] { 1, 2, 3 };
        await source.CompleteAsync(contentId, new MemoryStream(firstPayload), firstPayload.Length, "image/png", CancellationToken.None);

        var secondPayload = new byte[] { 9, 9, 9, 9, 9 };
        await source.CompleteAsync(contentId, new MemoryStream(secondPayload), secondPayload.Length, "image/png", CancellationToken.None);

        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, reloaded.DownloadStatus);
        Assert.Equal(firstPayload, reloaded.Content); // 第二次呼叫沒有覆寫掉第一次寫入的內容
    }

    [Fact]
    public async Task CompleteAsync_ContentAlreadyDownloading_SkipsWithoutThrowing()
    {
        // 認領失敗（狀態已經不是 Pending）要安靜跳過，不能拋例外把整個 ContentDownloadService
        // worker 拖垮
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Downloading }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var payload = new byte[] { 1, 2, 3 };
        var ex = await Record.ExceptionAsync(() =>
            source.CompleteAsync(groupMessage.Content!.Id, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None));

        Assert.Null(ex);
        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Downloading, reloaded.DownloadStatus); // 沒被改動，也沒被誤標 Completed
        Assert.Null(reloaded.Content);
    }

    // 體檢輪揪出的真 bug：認領（Pending→Downloading）之後若寫入失敗（長度不符、連線中斷等），
    // 一定要把狀態改回 Pending 再往外拋，否則 ProcessAsync 的重試迴圈下一次呼叫 CompleteAsync
    // 會因為認領不到（狀態已經不是 Pending）而靜默 return，重試被誤判成功、內容永遠卡在
    // Downloading——見 CompleteAsync 的 catch 區塊與 RevertClaimAsync 說明
    [Fact]
    public async Task CompleteAsync_DeclaredLengthExceedsActualStreamLength_ThrowsAndRevertsClaimToPending()
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
        var contentId = groupMessage.Content!.Id;

        // 宣稱 100 bytes，來源串流實際只有 3 bytes——模擬 LINE 回應的 Content-Length 標頭
        // 跟實際內容對不起來（見 CompleteAsync 對 contentLength 不可盡信的說明）
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.CompleteAsync(contentId, new MemoryStream([1, 2, 3]), 100, "image/png", CancellationToken.None));
        Assert.Contains("produced 3 bytes", ex.Message);

        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus); // 改回 Pending，不是卡在 Downloading

        // 證明真的能重新認領：下一次呼叫（模擬 ProcessAsync 的重試）用正確長度必須成功寫入，
        // 不能因為上次失敗留下的殘留狀態而靜默 no-op
        var payload = new byte[] { 9, 9, 9 };
        await source.CompleteAsync(contentId, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None);

        var completed = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, completed.DownloadStatus);
        Assert.Equal(payload, completed.Content);
    }

    [Fact]
    public async Task GetPendingIdsAsync_DownloadingContent_IsRecoveredAndResetToPending()
    {
        // 上次行程被殺時卡在 Downloading 的列——啟動接續要整批撿回並重設回 Pending，
        // 見 GetPendingIdsAsync 對 DownloadStatus.Downloading 的說明
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Downloading }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal(groupMessage.Content!.Id, Assert.Single(ids));
        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
    }

    [Fact]
    public async Task GetPendingIdsAsync_WithoutReclaim_LeavesDownloadingUntouched()
    {
        // 週期重掃時 worker 活著、Downloading 是真的在下載中：不能撈、也不能改回 Pending，
        // 否則另一個 worker 會再度認領同一顆 blob（CompleteAsync 的認領互斥就是為了擋這個）
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var downloading = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Downloading }
        };
        var pending = new GroupMessage
        {
            WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.AddRange(downloading, pending);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: false, CancellationToken.None);

        Assert.Equal(pending.Content!.Id, Assert.Single(ids));
        var reloaded = await dbContext.MessageContents.AsNoTracking().SingleAsync(c => c.Id == downloading.Content!.Id);
        Assert.Equal(DownloadStatus.Downloading, reloaded.DownloadStatus);
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

    // === 加密啟用時：CompleteAsync 寫進去的是分塊密文，不是明文（blob 不走 EF ValueConverter，
    // Content 屬性讀回來就是磁碟上的原始位元組，見 MessageDbContext 的說明）===

    private static readonly byte[] TestKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static FieldCipher EnabledCipher() => new(
        OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = Convert.ToBase64String(TestKey) }),
        NullLogger<FieldCipher>.Instance);

    private static byte[] DecryptStoredBlob(byte[] onDisk)
    {
        var header = onDisk.AsSpan(0, ChunkedBlobCipher.HeaderSize);
        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(header));
        var plaintextLength = ChunkedBlobCipher.ReadPlaintextLength(header);

        var result = new byte[plaintextLength];
        if (plaintextLength == 0)
        {
            return result;
        }

        var resultOffset = 0;
        var (_, lastChunkIndex) = ChunkedBlobCipher.ChunksCovering(0, plaintextLength, ChunkedBlobCipher.ChunkSize);
        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var (offset, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(i, plaintextLength, ChunkedBlobCipher.ChunkSize);
            var plaintextChunk = ChunkedBlobCipher.DecryptChunk(onDisk.AsSpan((int)offset, length), TestKey);
            plaintextChunk.CopyTo(result, resultOffset);
            resultOffset += plaintextChunk.Length;
        }

        return result;
    }

    [Fact]
    public async Task CompleteAsync_EncryptionEnabled_StoresChunkedCiphertext_NotPlaintext()
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
        var source = CreateSource(dbContext, cipher: EnabledCipher());

        var payload = new byte[] { 10, 20, 30, 40, 50 };
        await source.CompleteAsync(groupMessage.Content!.Id, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None);

        var reloaded = await ReloadContentAsync(groupMessage.Content.Id);
        Assert.NotEqual(payload, reloaded.Content); // 磁碟上不是明文
        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(reloaded.Content.AsSpan(0, ChunkedBlobCipher.HeaderSize)));
        Assert.Equal(payload, DecryptStoredBlob(reloaded.Content!));
    }

    [Fact]
    public async Task CompleteAsync_EncryptionEnabled_LargeMultiChunkPayload_RoundTripsExactly()
    {
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
        var source = CreateSource(dbContext, cipher: EnabledCipher());

        var payload = new byte[ChunkedBlobCipher.ChunkSize * 2 + 777];
        new Random(7).NextBytes(payload);

        await source.CompleteAsync(groupMessage.Content!.Id, new MemoryStream(payload), payload.Length, "video/mp4", CancellationToken.None);

        var reloaded = await ReloadContentAsync(groupMessage.Content.Id);
        Assert.Equal(
            ChunkedBlobCipher.ComputeEncryptedLength(payload.Length),
            reloaded.Content!.LongLength);
        Assert.Equal(payload, DecryptStoredBlob(reloaded.Content));
    }
}
