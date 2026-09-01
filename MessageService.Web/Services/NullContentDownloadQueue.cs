using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace MessageService.Services;

/// <summary>Line:OutboundHere=false 時註冊：這台主機不下載媒體，Enqueue 直接捨棄，
/// ReadAllAsync 永不產出（沒有 ContentDownloadService 在跑，也不會有人呼叫它，但保持
/// 型別完整是為了 IngestSideEffects 等呼叫端不必知道自己在哪個模式）。
///
/// 用捨棄實作取代真的 Channel 是刻意的：ContentDownloadQueue 是 Channel.CreateUnbounded，
/// 若這台主機仍會被 IIngestSink／IngestController 呼叫 Enqueue 卻沒有背景服務消費，
/// 會無上限累積造成記憶體洩漏。</summary>
public class NullContentDownloadQueue(ILogger<NullContentDownloadQueue> logger) : IContentDownloadQueue
{
    private int _warned;

    public void Enqueue(long messageContentId)
    {
        LogWarningOnce();
    }

    public void EnqueueDelayed(long messageContentId, TimeSpan delay, CancellationToken cancellationToken)
    {
        LogWarningOnce();
    }

    private void LogWarningOnce()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning("這台主機的 Line:OutboundHere 為 false，媒體下載的工作正被丟棄，由其他主機負責；若這台應該要下載，請檢查 Line:OutboundHere 設定。");
        }
    }

#pragma warning disable CS1998 // 沒有 await 是刻意的——永遠不產出任何項目
    public async IAsyncEnumerable<long> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield break;
    }
#pragma warning restore CS1998
}

