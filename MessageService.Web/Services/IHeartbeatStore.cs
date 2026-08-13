namespace MessageService.Services;

/// <summary>把一台主機的心跳寫進 HostHeartbeats（見該模型說明）。只在有資料庫的主機
/// （AllInOne／Core／Viewer）註冊——DbHeartbeatReporter 用它寫自己這台的心跳，
/// IngestController 的 heartbeat 端點用它代寫 Edge 送過來的心跳，兩者共用同一份 upsert 邏輯，
/// 不重複實作。</summary>
public interface IHeartbeatStore
{
    Task UpsertAsync(
        string role, string machineName, HeartbeatReport report, string? encryptionKeyFingerprint,
        CancellationToken cancellationToken);
}
