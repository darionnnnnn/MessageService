namespace MessageService.Options;

public class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    /// <summary>正式環境一律開啟，只有 WebAppFactoryFixture 這類測試主機會關掉——
    /// HeartbeatService 啟動後立刻回報一次（不等第一個 IntervalSeconds，讓儀表板馬上看得到
    /// 剛啟動的主機），這在真實部署是想要的行為，但在跑測試的 TestServer 裡會跟測試自己
    /// 準備的 HostHeartbeats 資料互相污染、造成間歇性失敗（背景服務跟測試斷言搶著寫/讀
    /// 同一張表）。其他背景服務（ContentDownloadService／OutboxForwarderService）能在測試裡
    /// 安全共存，是因為它們本來就靠 OutboundHere／ReceivesWebhook 等既有能力開關在測試設定下
    /// 被關掉；心跳所有模式都跑、沒有現成的開關可以借，所以額外加這個。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>多久回報一次存活狀態。所有部署模式都跑（見 Program.cs），失敗只記警告、
    /// 不影響其他背景服務——心跳本身不是關鍵路徑，見 HeartbeatService 說明。</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>outbox 最舊未死信項目滯留超過這個分鐘數就記一則 Error——批次A的 P0 那類
    /// 「Edge outbox 永久卡死但沒有任何告警」的情況，第一個小時內就會被叫出來，
    /// 見 OutboxForwarderService.LogDeadLetterCountAsync。</summary>
    public int OutboxBacklogAlertMinutes { get; set; } = 30;
}
