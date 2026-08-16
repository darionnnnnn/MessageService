namespace MessageService.Options;

public class ContentDownloadOptions
{
    public const string SectionName = "ContentDownload";

    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public int TranscodingPollSeconds { get; set; } = 5;
    public int TranscodingMaxPolls { get; set; } = 24;

    /// <summary>並行下載 worker 數；多個 worker 共讀同一個 Channel，避免一支大檔案（轉檔要等
    /// 最長 TranscodingPollSeconds × TranscodingMaxPolls 秒）卡住排在後面的圖片/檔案。</summary>
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>Failed 內容只在訊息到達後這麼多天內才會被 RequeuePendingAsync 重新撿回——
    /// LINE 的內容有保存期限，過期的檔案永遠下載不到，不該每次重啟都無限重跑。</summary>
    public int FailedRetryWindowDays { get; set; } = 7;

    /// <summary>單一內容累計失敗次數達這個門檻後，即使還在保留視窗內也不再重試。</summary>
    public int MaxFailedRetries { get; set; } = 10;

    /// <summary>週期性重掃的間隔分鐘數；設為 0 表示停用週期重掃，只保留啟動時那一次。</summary>
    public int RequeueIntervalMinutes { get; set; } = 15;

    /// <summary>認領租約的分鐘數；超過這個時間還停在 Downloading 就視為那台主機已經死了，可被回收重跑。</summary>
    public int ClaimLeaseMinutes { get; set; } = 60;
}
