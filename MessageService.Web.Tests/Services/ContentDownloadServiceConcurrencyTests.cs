using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

// ExecuteAsync 現在開 MaxConcurrency 個 worker 共讀同一個 Channel（P0-3：一支大檔案不該卡住
// 排在後面的圖片/檔案）。用一個會卡住 GetAsync 直到測試放行的假 work source 觀察「同時在飛
// 幾個」，比對固定時間的 wall-clock 睡眠可靠——同時在飛的數字達到上限後在被放行前不會再變。
public class ContentDownloadServiceConcurrencyTests
{
    private class GateTrackingWorkSource : IContentWorkSource
    {
        private readonly object _lock = new();
        private int _inFlight;
        public int MaxObservedConcurrency { get; private set; }
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<long>> GetPendingIdsAsync(bool reclaimDownloading, bool isStartup, string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public async Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _inFlight++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _inFlight);
            }

            await Gate.Task;

            lock (_lock)
            {
                _inFlight--;
            }

            return new ContentWorkItem(contentId, "line-msg", "image");
        }

        public Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, string ownerId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FailAsync(long contentId, string ownerId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_NeverExceedsMaxConcurrency_AndProcessesAllQueuedItems()
    {
        var workSource = new GateTrackingWorkSource();
        var services = new ServiceCollection();
        services.AddSingleton<IContentWorkSource>(workSource);
        services.AddSingleton<ILineContentClient>(new FakeLineContentClient());
        await using var provider = services.BuildServiceProvider();

        var queue = new ContentDownloadQueue(NullLogger<ContentDownloadQueue>.Instance);
        for (var i = 1; i <= 5; i++)
        {
            queue.Enqueue(i);
        }

        var service = new ContentDownloadService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions { MaxConcurrency = 2 }),
            NullLogger<ContentDownloadService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (workSource.MaxObservedConcurrency < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            // 5 個項目、MaxConcurrency=2：應該剛好 2 個同時卡在 Gate，不會是 1（沒真的並行）
            // 也不會超過 2（Channel 沒有限制住 worker 數）
            Assert.Equal(2, workSource.MaxObservedConcurrency);

            workSource.Gate.SetResult();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private class TypedWorkSource : IContentWorkSource
    {
        public HashSet<long> CompletedIds { get; } = [];

        public Task<IReadOnlyList<long>> GetPendingIdsAsync(bool reclaimDownloading, bool isStartup, string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken) =>
            Task.FromResult<ContentWorkItem?>(new ContentWorkItem(contentId, $"line-{contentId}", contentId == 4 ? "image" : "video"));

        public Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, string ownerId, CancellationToken cancellationToken)
        {
            CompletedIds.Add(contentId);
            return Task.CompletedTask;
        }

        public Task FailAsync(long contentId, string ownerId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_ThreeStuckVideos_DoesNotBlockImageProcessing()
    {
        // 問題5的回歸測試：轉檔還在處理中時舊版會原地睡在 WaitForTranscodingAsync，
        // 3 支影片會佔滿 MaxConcurrency=3 的所有 worker，讓排在後面的圖片永遠等不到 worker。
        // 新版每次只查一次轉檔狀態就用 EnqueueDelayed 回去排隊，worker 應該幾乎立刻空出來。
        // TranscodingPollSeconds 刻意設很長（60 秒）——如果圖片真的要等到那個延遲，這個測試
        // 的 5 秒 deadline 就會逾時，能有效區分「worker 沒被卡住」跟「worker 被卡住但剛好夠快」。
        var workSource = new TypedWorkSource();
        var contentClient = new FakeLineContentClient
        {
            OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Processing)
        };
        var services = new ServiceCollection();
        services.AddSingleton<IContentWorkSource>(workSource);
        services.AddSingleton<ILineContentClient>(contentClient);
        await using var provider = services.BuildServiceProvider();

        var queue = new ContentDownloadQueue(NullLogger<ContentDownloadQueue>.Instance);
        queue.Enqueue(1); // video，永遠 Processing
        queue.Enqueue(2); // video，永遠 Processing
        queue.Enqueue(3); // video，永遠 Processing
        queue.Enqueue(4); // image，不需要轉檔

        var service = new ContentDownloadService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions
            {
                MaxConcurrency = 3,
                TranscodingPollSeconds = 60,
                TranscodingMaxPolls = 100,
            }),
            NullLogger<ContentDownloadService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!workSource.CompletedIds.Contains(4) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Contains(4, workSource.CompletedIds);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
