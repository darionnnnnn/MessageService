using System.Runtime.CompilerServices;

namespace MessageService.Services;

/// <summary>Line:OutboundHere=false 時註冊，跟 NullContentDownloadQueue 是同一組理由——
/// 這台主機不刷新頭貼快取，Enqueue 直接捨棄，避免無消費者的 Channel 無上限累積。</summary>
public class NullProfileRefreshQueue : IProfileRefreshQueue
{
    public void Enqueue(ProfileRefreshTask task)
    {
        // 刻意什麼都不做
    }

#pragma warning disable CS1998 // 沒有 await 是刻意的——永遠不產出任何項目
    public async IAsyncEnumerable<ProfileRefreshTask> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield break;
    }
#pragma warning restore CS1998
}
