namespace MessageService.Models;

/// <summary>單一主機最近一次回報的存活狀態——主鍵 (Role, MachineName)，每台主機一列，upsert
/// 不成長，不需要保留期清除。Edge 沒有資料庫，由 Core 端代寫（見 IngestController 的
/// heartbeat 端點與 HttpHeartbeatReporter）；有資料庫的主機（AllInOne／Core／Viewer）自己
/// 直連寫入（見 DbHeartbeatReporter）。檢視端的「主機狀態」設定頁區塊純讀這張表，
/// 不會跨主機打 HTTP。</summary>
public class HostHeartbeat
{
    public required string Role { get; set; }
    public required string MachineName { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>只有收 webhook 的主機（AllInOne／Edge）才有 outbox 可回報，其餘主機固定 null。</summary>
    public long? OutboxPending { get; set; }

    /// <summary>最舊的未死信 outbox 項目距今幾秒——超過門檻代表排空可能卡住
    /// （見 OutboxForwarderService 的積壓告警），其餘主機固定 null。</summary>
    public double? OutboxOldestAgeSeconds { get; set; }

    /// <summary>金鑰的 SHA-256 前 4 bytes（見 FieldCipher.KeyId），未啟用加密固定 null。
    /// 多台直連資料庫的主機互相比對，金鑰設定不一致時能立刻看出來。</summary>
    public string? EncryptionKeyFingerprint { get; set; }
}
