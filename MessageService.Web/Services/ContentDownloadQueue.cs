using System.Threading.Channels;

namespace MessageService.Services;

public class ContentDownloadQueue(ILogger<ContentDownloadQueue> logger) : IContentDownloadQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long messageContentId) => _channel.Writer.TryWrite(messageContentId);

    public void EnqueueDelayed(long messageContentId, TimeSpan delay, CancellationToken cancellationToken)
    {
        // 受控的背景排程，不是裸 fire-and-forget：掛呼叫端（worker）的停機 token，
        // 且未預期例外會被記下來，不會悄悄消失讓「延遲重排看起來卡住了」卻查無原因
        _ = RunDelayedEnqueueAsync(messageContentId, delay, cancellationToken);
    }

    private async Task RunDelayedEnqueueAsync(long messageContentId, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            _channel.Writer.TryWrite(messageContentId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服務正在停機——這筆內容還在資料庫的 Pending 狀態，下次啟動的
            // ContentDownloadService.RequeuePendingAsync 會撈回，不用在這裡補寫
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to re-enqueue delayed content {MessageContentId}", messageContentId);
        }
    }

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
