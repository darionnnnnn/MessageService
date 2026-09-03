namespace MessageService.Options;

public class ProfileCacheOptions
{
    public const string SectionName = "ProfileCache";

    public TimeSpan RefreshAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>LINE profile API 呼叫失敗（暫時性故障、或 bot 已被踢出群組）後，同一個群組／
    /// 成員在這段冷卻時間內不會再被重新嘗試——沒有這個冷卻，GetStalenessAsync 只看 UpdatedAt，
    /// 失敗時完全不更新那個欄位，等於接下來每一則訊息都再打一次 LINE API，把單次故障放大成
    /// 持續性的無效呼叫。冷卻狀態存在程序記憶體，服務重啟就重置，不是正式的重試排程。</summary>
    public TimeSpan FailureRetryAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>背景補刷掃描間隔（分鐘）。目前唯一的刷新觸發是新訊息落地（IngestSideEffects），
    /// 安靜的群組或成員若當初抓取失敗或未及時刷新，就可能永遠停留在原始 Id 與代號圖示。
    /// 透過定期背景掃描主動將過期或缺圖的項目重新入列刷新。</summary>
    public int BackfillIntervalMinutes { get; set; } = 15;

    /// <summary>每次背景補刷掃描最多挑選的候選筆數（群組與成員合計）。
    /// 避免在資料庫過期資料量大時一次將過多任務塞入佇列造成流量突波。</summary>
    public int BackfillMaxPerScan { get; set; } = 50;
}
