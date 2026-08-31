namespace MessageService.Options;

/// <summary>
/// 訊息流監控與活性警示相關設定選項。
/// </summary>
public class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// 超過這個小時數沒有新訊息落地就在主機狀態頁標示警示；
    /// 預設 0 代表只顯示時刻、永不告警（每個環境的群組活躍度不同，程式不猜門檻）。
    /// </summary>
    public int MessageSilenceWarnHours { get; set; } = 0;
}