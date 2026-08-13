namespace MessageService.Options;

public class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    /// <summary>多久回報一次存活狀態。所有部署模式都跑（見 Program.cs），失敗只記警告、
    /// 不影響其他背景服務——心跳本身不是關鍵路徑，見 HeartbeatService 說明。</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>outbox 最舊未死信項目滯留超過這個分鐘數就記一則 Error——批次A的 P0 那類
    /// 「Edge outbox 永久卡死但沒有任何告警」的情況，第一個小時內就會被叫出來，
    /// 見 OutboxForwarderService.LogDeadLetterCountAsync。</summary>
    public int OutboxBacklogAlertMinutes { get; set; } = 30;
}
