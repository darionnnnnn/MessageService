namespace MessageService.Services;

/// <summary>Edge（沒有資料庫）打 Core 端 POST /api/ingest/heartbeat 的請求體——Edge 不碰
/// 加密金鑰，沒有指紋可回報，見 HostHeartbeat.EncryptionKeyFingerprint 的說明。</summary>
public record HeartbeatRequest(string Role, string MachineName, long? OutboxPending, double? OutboxOldestAgeSeconds);
