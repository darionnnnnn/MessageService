using System.Runtime.CompilerServices;

namespace MessageService.Services;

/// <summary>Line:OutboundHere=false 時註冊：這台主機不下載媒體，Enqueue 直接捨棄，
/// ReadAllAsync 永不產出（沒有 ContentDownloadService 在跑，也不會有人呼叫它，但保持
/// 型別完整是為了 IngestSideEffects 等呼叫端不必知道自己在哪個模式）。
///
/// 用捨棄實作取代真的 Channel 是刻意的：ContentDownloadQueue 是 Channel.CreateUnbounded，
/// 若這台主機仍會被 IIngestSink／IngestController 呼叫 Enqueue 卻沒有背景服務消費，
/// 會無上限累積造成記憶體洩漏。</summary>
public class NullContentDownloadQueue : IContentDownloadQueue
{
    public void Enqueue(long messageContentId)
    {
        // 刻意什麼都不做
    }

#pragma warning disable CS1998 // 沒有 await 是刻意的——永遠不產出任何項目
    public async IAsyncEnumerable<long> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield break;
    }
#pragma warning restore CS1998
}
