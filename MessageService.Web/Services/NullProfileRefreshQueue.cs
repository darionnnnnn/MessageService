using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace MessageService.Services;

/// <summary>Line:OutboundHere=false 時註冊，跟 NullContentDownloadQueue 是同一組理由——
/// 這台主機不刷新頭貼快取，Enqueue 直接捨棄，避免無消費者的 Channel 無上限累積。</summary>
public class NullProfileRefreshQueue(ILogger<NullProfileRefreshQueue> logger) : IProfileRefreshQueue
{
    private int _warned;

    public void Enqueue(ProfileRefreshTask task)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning("這台主機的 Line:OutboundHere 為 false，頭貼刷新的工作正被丟棄，由其他主機負責；若這台應該要下載，請檢查 Line:OutboundHere 設定。");
        }
    }

#pragma warning disable CS1998 // 沒有 await 是刻意的——永遠不產出任何項目
    public async IAsyncEnumerable<ProfileRefreshTask> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield break;
    }
#pragma warning restore CS1998
}

