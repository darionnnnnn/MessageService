namespace MessageService.Services;

public interface IContentDownloadQueue
{
    void Enqueue(long messageContentId);

    /// <summary>等 delay 過後才入列——轉檔還在處理中時用這個，讓呼叫端（worker）立刻回去
    /// 服務佇列裡的下一個項目，不必自己睡在原地等。</summary>
    void EnqueueDelayed(long messageContentId, TimeSpan delay, CancellationToken cancellationToken);

    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);
}
