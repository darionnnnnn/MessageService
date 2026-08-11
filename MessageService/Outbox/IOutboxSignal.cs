namespace MessageService.Outbox;

/// <summary>叫醒 forwarder 的「門鈴」：寫入 outbox 後立刻通知一次，讓 forwarder 不用等到下一次
/// 輪詢間隔就能處理新項目。輪詢仍然保留（見 OutboxForwarderService），用來撿回退避到期的
/// 重試項目——門鈴只解決「新項目」的延遲，不解決「舊項目重試時間到了」的喚醒。</summary>
public interface IOutboxSignal
{
    void NotifyNewEntry();

    /// <summary>等到下一次門鈴，或逾時（逾時視為正常，呼叫端會接著跑一次輪詢）。</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
