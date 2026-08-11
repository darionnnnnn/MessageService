namespace MessageService.Options;

public class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>outbox 空的時候，最長多久沒有新項目就醒來重掃一次（用來撿回 NextAttemptAt 已到期的重試項目）。
    /// 寫入 outbox 會立刻叫醒 forwarder，這個值只是保底，不是正常延遲。</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>一次最多處理幾筆，避免單輪把整個 outbox 掃進記憶體。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>重試退避的基準秒數，第 N 次失敗延遲約 BaseRetryDelaySeconds × N（封頂 MaxRetryDelaySeconds）。</summary>
    public int BaseRetryDelaySeconds { get; set; } = 5;

    public int MaxRetryDelaySeconds { get; set; } = 300;
}
