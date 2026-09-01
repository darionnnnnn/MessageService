using MessageService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

// Line:OutboundHere=false 時註冊的捨棄實作——核心承諾是「Enqueue 之後 ReadAllAsync 永遠不會
// 產出任何東西」，不管呼叫幾次 Enqueue。這是防止 Channel.CreateUnbounded 在沒有消費者時
// 無上限累積記憶體的關鍵，值得直接測。
public class NullQueuesTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task NullContentDownloadQueue_EnqueueThenReadAll_NeverYieldsAnything()
    {
        var queue = new NullContentDownloadQueue(NullLogger<NullContentDownloadQueue>.Instance);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var items = new List<long>();
        await foreach (var id in queue.ReadAllAsync(cts.Token))
        {
            items.Add(id);
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task NullProfileRefreshQueue_EnqueueThenReadAll_NeverYieldsAnything()
    {
        var queue = new NullProfileRefreshQueue(NullLogger<NullProfileRefreshQueue>.Instance);
        queue.Enqueue(new ProfileRefreshTask("G1", "U1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var items = new List<ProfileRefreshTask>();
        await foreach (var task in queue.ReadAllAsync(cts.Token))
        {
            items.Add(task);
        }

        Assert.Empty(items);
    }

    [Fact]
    public void NullContentDownloadQueue_FirstEnqueue_LogsWarning_SecondEnqueue_DoesNotLogAgain()
    {
        var logger = new CapturingLogger<NullContentDownloadQueue>();
        var queue = new NullContentDownloadQueue(logger);

        queue.Enqueue(1);

        Assert.Single(logger.Warnings);
        Assert.Contains("Line:OutboundHere 為 false", logger.Warnings[0]);
        Assert.Contains("媒體下載", logger.Warnings[0]);

        queue.Enqueue(2);

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void NullContentDownloadQueue_EnqueueDelayed_SharesWarnedFlagWithEnqueue()
    {
        var logger = new CapturingLogger<NullContentDownloadQueue>();
        var queue = new NullContentDownloadQueue(logger);

        queue.EnqueueDelayed(1, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Single(logger.Warnings);
        Assert.Contains("Line:OutboundHere 為 false", logger.Warnings[0]);
        Assert.Contains("媒體下載", logger.Warnings[0]);

        queue.Enqueue(2);

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void NullProfileRefreshQueue_FirstEnqueue_LogsWarning_SecondEnqueue_DoesNotLogAgain()
    {
        var logger = new CapturingLogger<NullProfileRefreshQueue>();
        var queue = new NullProfileRefreshQueue(logger);

        queue.Enqueue(new ProfileRefreshTask("G1", "U1"));

        Assert.Single(logger.Warnings);
        Assert.Contains("Line:OutboundHere 為 false", logger.Warnings[0]);
        Assert.Contains("頭貼刷新", logger.Warnings[0]);

        queue.Enqueue(new ProfileRefreshTask("G2", "U2"));

        Assert.Single(logger.Warnings);
    }
}

