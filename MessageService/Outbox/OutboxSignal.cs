using System.Threading.Channels;

namespace MessageService.Outbox;

public class OutboxSignal : IOutboxSignal
{
    // 容量 1、滿了就丟舊的：門鈴只需要「有沒有新事件」這個布林狀態，不需要每次寫入都排隊喚醒一次
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void NotifyNewEntry() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await _channel.Reader.ReadAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 逾時是正常路徑：沒有新門鈴事件，讓呼叫端接著跑保底輪詢
        }
    }
}
