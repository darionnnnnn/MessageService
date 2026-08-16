using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private DbContentWorkSource CreateSource(
        MessageDbContext dbContext,
        ContentDownloadOptions? options = null,
        FieldCipher? cipher = null,
        ILogger<DbContentWorkSource>? logger = null) =>
        new(dbContext, OptionsFactory.Create(options ?? new ContentDownloadOptions()), cipher ?? FieldCipher.Disabled, logger ?? NullLogger<DbContentWorkSource>.Instance);

    // 用全新的 scope／DbContext 重新查詢——CompleteAsync 對 blob 欄位是繞過 EF change tracker
    // 直接下 raw ADO 指令寫入的（見類別說明），沿用同一個 DbContext 讀回來會拿到查詢前就已經
    // 追蹤、沒被更新過的舊實體（EF 的 identity map 不會用查詢結果覆寫已追蹤實體的屬性）
    private async Task<MessageContent> ReloadContentAsync(long id)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.MessageContents.Include(c => c.Blob).SingleAsync(c => c.Id == id);
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

    private async Task<long> SeedDownloadingContentAsync(DateTimeOffset? claimedAt)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(),
            LineMessageId = $"line-{Guid.NewGuid():N}",
            GroupId = "G1",
            MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent
            {
                DownloadStatus = DownloadStatus.Downloading,
                ClaimedAt = claimedAt
            }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        return groupMessage.Content!.Id;
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
        Assert.Equal(firstPayload, reloaded.Blob?.Content); // 第二次呼叫沒有覆寫掉第一次寫入的內容
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
        var reloaded = await dbContext.MessageContents.Include(c => c.Blob).AsNoTracking().SingleAsync(c => c.Id == groupMessage.Content.Id);
        Assert.Equal(DownloadStatus.Downloading, reloaded.DownloadStatus); // 沒被改動，也沒被誤標 Completed
        Assert.Null(reloaded.Blob);
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
        Assert.Equal(payload, completed.Blob?.Content);
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

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await source.FailAsync(groupMessage.Content!.Id, CancellationToken.None);

        var reloaded = await ReloadContentAsync(groupMessage.Content.Id);
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
        Assert.Equal(payload, reloaded.Blob?.Content);
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
        Assert.Equal(payload, reloaded.Blob?.Content);
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
        Assert.NotEqual(payload, reloaded.Blob?.Content); // 磁碟上不是明文
        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(reloaded.Blob!.Content.AsSpan(0, ChunkedBlobCipher.HeaderSize)));
        Assert.Equal(payload, DecryptStoredBlob(reloaded.Blob.Content!));
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
            reloaded.Blob!.Content.LongLength);
        Assert.Equal(payload, DecryptStoredBlob(reloaded.Blob.Content));
    }

    // ==== 任務要求驗收測試：原子累加、ChangeTracker 隔離、邊界與失敗路徑 ====

    [Fact]
    public async Task FailAsync_ConcurrentCalls_AtomicallyIncrementsFailedAttempts()
    {
        var (dbContext, contentId) = await SeedFailedContentAsync(DateTimeOffset.UtcNow, failedAttempts: 0);

        using var scope1 = _provider.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source1 = CreateSource(db1);

        using var scope2 = _provider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source2 = CreateSource(db2);

        await Task.WhenAll(
            source1.FailAsync(contentId, CancellationToken.None),
            source2.FailAsync(contentId, CancellationToken.None));

        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
        Assert.Equal(2, reloaded.FailedAttempts);
    }

    [Fact]
    public async Task CompleteAsync_Success_DoesNotTrackMessageContentEntity()
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
        var contentId = groupMessage.Content!.Id;

        // 清除先前 Add/SaveChanges 的追蹤狀態
        dbContext.ChangeTracker.Clear();
        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());

        var source = CreateSource(dbContext);
        var payload = new byte[] { 1, 2, 3 };
        await source.CompleteAsync(contentId, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None);

        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, reloaded.DownloadStatus);
        Assert.Equal(payload, reloaded.Blob?.Content);
    }

    [Fact]
    public async Task FailAsync_Success_DoesNotTrackMessageContentEntity()
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
        var contentId = groupMessage.Content!.Id;

        dbContext.ChangeTracker.Clear();
        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());

        var source = CreateSource(dbContext);
        await source.FailAsync(contentId, CancellationToken.None);

        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
        Assert.Equal(1, reloaded.FailedAttempts);
    }

    [Theory]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Completed)]
    public async Task GetAsync_NonPendingContent_ReturnsNull(DownloadStatus status)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "line-m1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = status }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var source = CreateSource(dbContext);

        var item = await source.GetAsync(groupMessage.Content!.Id, CancellationToken.None);

        Assert.Null(item);
    }

    [Fact]
    public async Task GetAsync_GroupMessageNotExists_ReturnsNull()
    {
        // 情況 1：contentId 完全不存在（當然也沒有關聯的 GroupMessage）
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext);

        var nonExistent = await source.GetAsync(999999, CancellationToken.None);
        Assert.Null(nonExistent);

        // 情況 2：暫時關閉 FK 限制插入一筆沒有對應 GroupMessage 的孤兒 MessageContent 列
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO MessageContents (GroupMessageId, DownloadStatus, FailedAttempts) VALUES (999999, 'Pending', 0);");
            var orphanId = await dbContext.MessageContents
                .Where(c => c.GroupMessageId == 999999)
                .Select(c => c.Id)
                .SingleAsync();

            var orphanItem = await source.GetAsync(orphanId, CancellationToken.None);
            Assert.Null(orphanItem);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    [Fact]
    public async Task GetAsync_NonExistentContentId_ReturnsNull()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext);

        var item = await source.GetAsync(999999, CancellationToken.None);

        Assert.Null(item);
    }

    [Fact]
    public async Task GetAsync_PendingContent_DoesNotTrackEntities()
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
        var contentId = groupMessage.Content!.Id;

        dbContext.ChangeTracker.Clear();
        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());
        Assert.Empty(dbContext.ChangeTracker.Entries<GroupMessage>());

        var source = CreateSource(dbContext);
        var item = await source.GetAsync(contentId, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal("line-m1", item!.LineMessageId);
        Assert.Empty(dbContext.ChangeTracker.Entries<MessageContent>());
        Assert.Empty(dbContext.ChangeTracker.Entries<GroupMessage>());
    }

    [Fact]
    public async Task CompleteAsync_MetadataUpdateFails_LogsErrorAndRethrows_WithoutRevertingClaim()
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
        var contentId = groupMessage.Content!.Id;

        var logger = new CapturingLogger();
        var source = CreateSource(dbContext, logger: logger);

        using var cts = new CancellationTokenSource();
        var payload = new byte[] { 1, 2, 3 };
        // 自訂串流：讀取完畢後取消 token，讓後續的 ExecuteUpdateAsync（中繼資料更新）拋出例外
        var triggerStream = new ActionOnReadStream(new MemoryStream(payload), () => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.CompleteAsync(contentId, triggerStream, payload.Length, "image/png", cts.Token));

        // 驗證有記錄 Error log 且包含 contentId 與錯誤描述
        Assert.Single(logger.Errors);
        Assert.Contains(contentId.ToString(), logger.Errors[0]);
        Assert.Contains("blob 已寫入，但中繼資料更新失敗", logger.Errors[0]);

        // 驗證狀態維持在 Downloading（沒有被 RevertClaim 成 Pending）
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Downloading, reloaded.DownloadStatus);
        Assert.Equal(payload, reloaded.Blob?.Content); // blob 確實已寫入
    }

    [Fact]
    public async Task CompleteAsync_ConsecutiveCallsWithDifferentLengths_CleanlyReplacesBlobRow()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(), LineMessageId = "m-retry", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var contentId = groupMessage.Content!.Id;
        var source = CreateSource(dbContext);

        // 第一次寫入（較長內容）
        var payload1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        await source.CompleteAsync(contentId, new MemoryStream(payload1), payload1.Length, "image/png", CancellationToken.None);

        var firstResult = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, firstResult.DownloadStatus);
        Assert.Equal(payload1, firstResult.Blob?.Content);

        // 模擬重試：將狀態重設為 Pending
        using (var updateScope = _provider.CreateScope())
        {
            var updateDb = updateScope.ServiceProvider.GetRequiredService<MessageDbContext>();
            await updateDb.MessageContents.Where(c => c.Id == contentId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DownloadStatus, DownloadStatus.Pending));
        }

        // 第二次寫入（不同長度，較短內容）
        var payload2 = new byte[] { 99, 88, 77 };
        await source.CompleteAsync(contentId, new MemoryStream(payload2), payload2.Length, "image/jpeg", CancellationToken.None);

        // 驗證 MessageContentBlobs 只有 1 列，且內容等於第二次寫入的位元組
        using (var verifyScope = _provider.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
            var blobCount = await verifyDb.MessageContentBlobs.CountAsync(b => b.MessageContentId == contentId);
            Assert.Equal(1, blobCount);
        }

        var secondResult = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, secondResult.DownloadStatus);
        Assert.Equal(payload2, secondResult.Blob?.Content);
    }

    [Fact]
    public async Task FailAsync_DeletesLingeringBlobRowInMessageContentBlobs()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(), LineMessageId = "m-fail", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var contentId = groupMessage.Content!.Id;

        // 手動插入一筆殘留的 blob（模擬下載中途失敗或先前殘留的 zeroblob 列）
        dbContext.MessageContentBlobs.Add(new MessageContentBlob
        {
            MessageContentId = contentId,
            Content = [1, 2, 3, 4, 5]
        });
        await dbContext.SaveChangesAsync();

        // 確認 blob 列存在
        var initialBlobCount = await dbContext.MessageContentBlobs.CountAsync(b => b.MessageContentId == contentId);
        Assert.Equal(1, initialBlobCount);

        var source = CreateSource(dbContext);
        await source.FailAsync(contentId, CancellationToken.None);

        // 驗證狀態被標為 Failed，且 MessageContentBlobs 裡的列已完全被刪除
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
        Assert.Null(reloaded.Blob);

        using var verifyScope = _provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var remainingBlobCount = await verifyDb.MessageContentBlobs.CountAsync(b => b.MessageContentId == contentId);
        Assert.Equal(0, remainingBlobCount);
    }

    // ==== 任務 C2 新增測試：認領租約逾期回收與 ClaimedAt 生命週期驗證 ====

    [Fact]
    public async Task GetPendingIdsAsync_DownloadingWithUnexpiredLease_IsNotRequeued()
    {
        // 租約未逾期的 Downloading：表示仍有其他主機或 worker 在下載中，不改狀態、也不出現在待處理清單
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var contentId = await SeedDownloadingContentAsync(claimedAt);
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext, new ContentDownloadOptions { ClaimLeaseMinutes = 60 });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.DoesNotContain(contentId, ids);
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Downloading, reloaded.DownloadStatus);
        Assert.NotNull(reloaded.ClaimedAt);
        Assert.True(Math.Abs((reloaded.ClaimedAt.Value - claimedAt).TotalSeconds) < 1);
    }

    [Fact]
    public async Task GetPendingIdsAsync_DownloadingWithExpiredLease_IsRequeuedAndClaimedAtResetToNull()
    {
        // 租約已逾期的 Downloading（例如 70 分鐘前認領，租約 60 分鐘）：應改回 Pending 且 ClaimedAt 變為 null，並出現在回傳清單
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-70);
        var contentId = await SeedDownloadingContentAsync(claimedAt);
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext, new ContentDownloadOptions { ClaimLeaseMinutes = 60 });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Contains(contentId, ids);
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
        Assert.Null(reloaded.ClaimedAt);
    }

    [Fact]
    public async Task GetPendingIdsAsync_DownloadingWithNullClaimedAt_IsRequeuedAndClaimedAtResetToNull()
    {
        // ClaimedAt 為 null 的 Downloading（舊版資料或未設租約的殘留）：視為逾期回收，改回 Pending 且出現在清單中
        var contentId = await SeedDownloadingContentAsync(claimedAt: null);
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext, new ContentDownloadOptions { ClaimLeaseMinutes = 60 });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Contains(contentId, ids);
        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
        Assert.Null(reloaded.ClaimedAt);
    }

    [Fact]
    public async Task CompleteAsync_Success_WritesClaimedAtDuringClaim_AndClearsClaimedAtUponCompletion()
    {
        // 1. 驗證認領成功時 ClaimedAt 被寫入：取消 token 中斷中繼資料更新，讓狀態留在認領後階段
        using var scope1 = _provider.CreateScope();
        var dbContext1 = scope1.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage1 = new GroupMessage
        {
            WebhookEventId = "e-claim-test-1", LineMessageId = "m-claim-test-1", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext1.GroupMessages.Add(groupMessage1);
        await dbContext1.SaveChangesAsync();
        var contentId1 = groupMessage1.Content!.Id;

        using var cts = new CancellationTokenSource();
        var payload = new byte[] { 1, 2, 3 };
        var triggerStream = new ActionOnReadStream(new MemoryStream(payload), () => cts.Cancel());
        var beforeClaim = DateTimeOffset.UtcNow.AddSeconds(-2);
        var source1 = CreateSource(dbContext1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source1.CompleteAsync(contentId1, triggerStream, payload.Length, "image/png", cts.Token));

        var claimedContent = await ReloadContentAsync(contentId1);
        Assert.Equal(DownloadStatus.Downloading, claimedContent.DownloadStatus);
        Assert.NotNull(claimedContent.ClaimedAt);
        Assert.True(claimedContent.ClaimedAt >= beforeClaim);

        // 2. 驗證完整完成後 ClaimedAt 被清空為 null
        using var scope2 = _provider.CreateScope();
        var dbContext2 = scope2.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage2 = new GroupMessage
        {
            WebhookEventId = "e-claim-test-2", LineMessageId = "m-claim-test-2", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext2.GroupMessages.Add(groupMessage2);
        await dbContext2.SaveChangesAsync();
        var contentId2 = groupMessage2.Content!.Id;

        var source2 = CreateSource(dbContext2);
        await source2.CompleteAsync(contentId2, new MemoryStream(payload), payload.Length, "image/png", CancellationToken.None);

        var completedContent = await ReloadContentAsync(contentId2);
        Assert.Equal(DownloadStatus.Completed, completedContent.DownloadStatus);
        Assert.Null(completedContent.ClaimedAt);
    }

    [Fact]
    public async Task CompleteAsync_StreamThrows_RevertsClaimAndResetsClaimedAtToNull()
    {
        // 寫入失敗時回退認領：狀態改回 Pending 且 ClaimedAt 設回 null
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupMessage = new GroupMessage
        {
            WebhookEventId = "e-revert-test", LineMessageId = "m-revert-test", GroupId = "G1", MessageType = "image",
            EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        var contentId = groupMessage.Content!.Id;

        var source = CreateSource(dbContext);
        // 宣告長度 50 但串流只有 3 bytes -> 觸發 InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.CompleteAsync(contentId, new MemoryStream([1, 2, 3]), 50, "image/png", CancellationToken.None));

        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Pending, reloaded.DownloadStatus);
        Assert.Null(reloaded.ClaimedAt);
    }

    [Fact]
    public async Task FailAsync_FromDownloadingWithClaimedAt_ClearsClaimedAtAndSetsFailed()
    {
        // 失敗標記時（無論原本是 Downloading 還是 Pending）：ClaimedAt 被清空為 null
        var claimedAt = DateTimeOffset.UtcNow;
        var contentId = await SeedDownloadingContentAsync(claimedAt);
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var source = CreateSource(dbContext);

        await source.FailAsync(contentId, CancellationToken.None);

        var reloaded = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, reloaded.DownloadStatus);
        Assert.Null(reloaded.ClaimedAt);
    }

    private sealed class CapturingLogger : ILogger<DbContentWorkSource>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }

    private sealed class ActionOnReadStream(Stream inner, Action onEof) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0) onEof();
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read == 0) onEof();
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (read == 0) onEof();
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
