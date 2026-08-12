using MessageService.Services;

namespace MessageService.Tests.Services;

// Line:OutboundHere=false 時註冊的捨棄實作——核心承諾是「Enqueue 之後 ReadAllAsync 永遠不會
// 產出任何東西」，不管呼叫幾次 Enqueue。這是防止 Channel.CreateUnbounded 在沒有消費者時
// 無上限累積記憶體的關鍵，值得直接測。
public class NullQueuesTests
{
    [Fact]
    public async Task NullContentDownloadQueue_EnqueueThenReadAll_NeverYieldsAnything()
    {
        var queue = new NullContentDownloadQueue();
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
        var queue = new NullProfileRefreshQueue();
        queue.Enqueue(new ProfileRefreshTask("G1", "U1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var items = new List<ProfileRefreshTask>();
        await foreach (var task in queue.ReadAllAsync(cts.Token))
        {
            items.Add(task);
        }

        Assert.Empty(items);
    }
}
