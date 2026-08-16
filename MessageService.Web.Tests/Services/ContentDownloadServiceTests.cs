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

public class ContentDownloadServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeLineContentClient _contentClient = new();

    public ContentDownloadServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IContentWorkSource, DbContentWorkSource>();
        services.AddSingleton<ILineContentClient>(_contentClient);
        services.AddSingleton(OptionsFactory.Create(new ContentDownloadOptions()));
        services.AddSingleton(FieldCipher.Disabled);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ContentDownloadService CreateService(ContentDownloadOptions? options = null) =>
        CreateServiceWithQueue(out _, options);

    private ContentDownloadService CreateServiceWithQueue(out FakeContentDownloadQueue queue, ContentDownloadOptions? options = null)
    {
        queue = new FakeContentDownloadQueue();
        return new ContentDownloadService(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new ContentDownloadOptions
            {
                MaxRetries = 3,
                RetryDelayMilliseconds = 0,
                TranscodingPollSeconds = 0,
                TranscodingMaxPolls = 5
            }),
            NullLogger<ContentDownloadService>.Instance);
    }

    private async Task<long> SeedPendingContentAsync(string messageType, string lineMessageId = "line-msg-1", string? stickerId = null)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(),
            LineMessageId = lineMessageId,
            GroupId = "G1",
            MessageType = messageType,
            StickerId = stickerId,
            EventTimestamp = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        return groupMessage.Content!.Id;
    }

    private async Task<MessageContent> ReloadContentAsync(long id)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.MessageContents.SingleAsync(c => c.Id == id);
    }

    [Fact]
    public async Task ProcessAsync_ImageDownloadSucceeds_MarksCompleted()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => Task.FromResult(new LineContentResult(new MemoryStream([1, 2, 3, 4]), "image/jpeg", 4));

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, content.Content);
        Assert.Equal("image/jpeg", content.ContentType);
        Assert.NotNull(content.CompletedAt);
        Assert.Single(_contentClient.ContentCalls);
        Assert.Empty(_contentClient.StickerCalls);
    }

    [Fact]
    public async Task ProcessAsync_Sticker_DownloadsFromStickerCdn()
    {
        var contentId = await SeedPendingContentAsync("sticker", stickerId: "123456");
        _contentClient.OnGetSticker = _ => Task.FromResult(new LineContentResult(new MemoryStream([1, 2]), "image/png", 2));

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.Single(_contentClient.StickerCalls);
        Assert.Empty(_contentClient.ContentCalls);
        Assert.Equal("123456", _contentClient.StickerCalls[0]);
    }

    [Fact]
    public async Task ProcessAsync_StickerWithoutId_MarksFailedWithoutRetries()
    {
        var contentId = await SeedPendingContentAsync("sticker", stickerId: null);

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Empty(_contentClient.ContentCalls);
        Assert.Empty(_contentClient.StickerCalls);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ProcessAsync_VideoOrAudio_TranscodingAlreadySucceeded_DownloadsImmediately(string messageType)
    {
        var contentId = await SeedPendingContentAsync(messageType);
        _contentClient.OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Succeeded);

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.Single(_contentClient.ContentCalls);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ProcessAsync_TranscodingStillProcessing_ReturnsImmediately_EnqueuesDelayedRecheck_DoesNotDownload(string messageType)
    {
        // 問題5：查一次就回去服務下一筆，不該在這裡原地睡等——worker 才不會被一支還在轉檔的
        // 影片卡住，見 ContentDownloadServiceConcurrencyTests 的並發回歸測試
        var contentId = await SeedPendingContentAsync(messageType);
        _contentClient.OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Processing);

        var service = CreateServiceWithQueue(out var queue, new ContentDownloadOptions
        {
            TranscodingPollSeconds = 7,
            TranscodingMaxPolls = 24,
        });
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Pending, content.DownloadStatus); // 還沒下載，也還沒 Fail
        Assert.Empty(_contentClient.ContentCalls);
        var delayed = Assert.Single(queue.EnqueuedDelayed);
        Assert.Equal(contentId, delayed.MessageContentId);
        Assert.Equal(TimeSpan.FromSeconds(7), delayed.Delay);
        Assert.Single(_contentClient.TranscodingCalls);
    }

    [Fact]
    public async Task ProcessAsync_TranscodingStillProcessing_ReachesPollLimit_MarksFailed()
    {
        var contentId = await SeedPendingContentAsync("video");
        _contentClient.OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Processing);
        var service = CreateServiceWithQueue(out var queue, new ContentDownloadOptions { TranscodingMaxPolls = 3 });

        // 模擬同一個 messageContentId 被重排回來查詢三次（達到上限），
        // 直接重複呼叫 ProcessAsync 模擬 EnqueueDelayed 觸發後 worker 再次撿到同一筆
        for (var i = 0; i < 3; i++)
        {
            await service.ProcessAsync(contentId, CancellationToken.None);
        }

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Empty(_contentClient.ContentCalls);
        // 第三次（達到上限）直接判定 Failed，不會再排一次延遲重查
        Assert.Equal(2, queue.EnqueuedDelayed.Count);
    }

    [Fact]
    public async Task ProcessAsync_TranscodingSucceedsAfterPriorStillProcessingCalls_Downloads()
    {
        // 同一顆 messageContentId 先被查到 Processing（poll count 累加），下一次 worker
        // 撿回來查到 Succeeded——驗證 poll 計數本身不影響「轉檔成功就正常下載」這條路徑
        var contentId = await SeedPendingContentAsync("video");
        var callCount = 0;
        _contentClient.OnGetTranscodingStatus = _ =>
        {
            callCount++;
            return Task.FromResult(callCount < 2 ? TranscodingStatus.Processing : TranscodingStatus.Succeeded);
        };
        var service = CreateService();

        await service.ProcessAsync(contentId, CancellationToken.None); // Processing
        await service.ProcessAsync(contentId, CancellationToken.None); // Succeeded → 下載

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.Single(_contentClient.ContentCalls);
    }

    [Fact]
    public async Task ProcessAsync_VideoTranscodingFailed_MarksFailedWithoutDownloading()
    {
        var contentId = await SeedPendingContentAsync("video");
        _contentClient.OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Failed);

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Empty(_contentClient.ContentCalls);
    }

    [Fact]
    public async Task ProcessAsync_DownloadFailsBeyondMaxRetries_MarksFailed()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => throw new HttpRequestException("network error");

        var service = CreateService(new ContentDownloadOptions { MaxRetries = 3, RetryDelayMilliseconds = 0 });
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Equal(3, _contentClient.ContentCalls.Count);
    }

    [Fact]
    public async Task RequeuePendingAsync_EnqueuesExistingPendingContent()
    {
        var contentId = await SeedPendingContentAsync("image");
        var queue = new FakeContentDownloadQueue();
        var service = new ContentDownloadService(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions()),
            NullLogger<ContentDownloadService>.Instance);

        await service.RequeuePendingAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(queue.Enqueued));
    }

    [Fact]
    public async Task RequeuePendingAsync_ResetsFailedContentToPendingAndEnqueues()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => throw new HttpRequestException("bad token");
        var failingService = CreateService(new ContentDownloadOptions { MaxRetries = 1, RetryDelayMilliseconds = 0 });
        await failingService.ProcessAsync(contentId, CancellationToken.None);
        Assert.Equal(DownloadStatus.Failed, (await ReloadContentAsync(contentId)).DownloadStatus);

        var queue = new FakeContentDownloadQueue();
        var service = new ContentDownloadService(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions()),
            NullLogger<ContentDownloadService>.Instance);

        await service.RequeuePendingAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(queue.Enqueued));
        Assert.Equal(DownloadStatus.Pending, (await ReloadContentAsync(contentId)).DownloadStatus);
    }

    private (ContentDownloadService Service, FakeContentWorkSource WorkSource, FakeContentDownloadQueue Queue) CreateServiceWithFakeWorkSource(
        ContentDownloadOptions? options = null)
    {
        var workSource = new FakeContentWorkSource();
        var services = new ServiceCollection();
        services.AddSingleton<IContentWorkSource>(workSource);
        services.AddSingleton<ILineContentClient>(_contentClient);
        var provider = services.BuildServiceProvider();

        var queue = new FakeContentDownloadQueue();
        var service = new ContentDownloadService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new ContentDownloadOptions
            {
                MaxRetries = 3,
                RetryDelayMilliseconds = 0,
                TranscodingPollSeconds = 0,
                TranscodingMaxPolls = 5
            }),
            NullLogger<ContentDownloadService>.Instance);

        return (service, workSource, queue);
    }

    [Fact]
    public async Task RunPeriodicRequeueAsync_WhenIntervalElapses_QueriesWorkSourceAgain()
    {
        var (service, workSource, queue) = CreateServiceWithFakeWorkSource();
        workSource.PendingIds = [10, 20];

        using var cts = new CancellationTokenSource();
        var loopTask = service.RunPeriodicRequeueAsync(TimeSpan.FromMilliseconds(20), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (workSource.GetPendingIdsCallCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await loopTask;

        Assert.True(workSource.GetPendingIdsCallCount >= 2);
        Assert.Contains(10, queue.Enqueued);
        Assert.Contains(20, queue.Enqueued);
        // 週期重掃一律不撿 Downloading（worker 正在跑）
        Assert.All(workSource.ReclaimDownloadingCalls, reclaim => Assert.False(reclaim));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequeueIntervalMinutesIsZero_QueriesWorkSourceOnlyOnceAtStartup()
    {
        var workSource = new FakeContentWorkSource { PendingIds = [1] };
        var services = new ServiceCollection();
        services.AddSingleton<IContentWorkSource>(workSource);
        services.AddSingleton<ILineContentClient>(_contentClient);
        var provider = services.BuildServiceProvider();

        var queue = new ContentDownloadQueue(NullLogger<ContentDownloadQueue>.Instance);
        var service = new ContentDownloadService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions
            {
                RequeueIntervalMinutes = 0,
                MaxConcurrency = 1
            }),
            NullLogger<ContentDownloadService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (workSource.GetPendingIdsCallCount < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(1, workSource.GetPendingIdsCallCount);
            // 啟動那次要撿回上次行程留下的 Downloading 孤兒
            Assert.Equal([true], workSource.ReclaimDownloadingCalls);

            await Task.Delay(100);

            Assert.Equal(1, workSource.GetPendingIdsCallCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunPeriodicRequeueAsync_WhenScanThrowsException_ContinuesToNextIteration()
    {
        var (service, workSource, queue) = CreateServiceWithFakeWorkSource();
        var callIndex = 0;
        workSource.OnGetPendingIds = _ =>
        {
            callIndex++;
            if (callIndex == 1)
            {
                throw new InvalidOperationException("Simulated transient error during scan");
            }
            return Task.FromResult<IReadOnlyList<long>>([42]);
        };

        using var cts = new CancellationTokenSource();
        var loopTask = service.RunPeriodicRequeueAsync(TimeSpan.FromMilliseconds(20), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (workSource.GetPendingIdsCallCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await loopTask;

        Assert.True(workSource.GetPendingIdsCallCount >= 2);
        Assert.Contains(42, queue.Enqueued);
    }

    [Fact]
    public async Task RunPeriodicRequeueAsync_WhenIntervalIsZeroOrNegative_ReturnsImmediately()
    {
        var (service, workSource, _) = CreateServiceWithFakeWorkSource();
        await service.RunPeriodicRequeueAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(0, workSource.GetPendingIdsCallCount);
    }
}

