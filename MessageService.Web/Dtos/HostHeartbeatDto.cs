namespace MessageService.Web.Dtos;

/// <summary>Status 在伺服器端算好（"Online"／"Delayed"／"Offline"）而不是交給前端拿
/// LastSeenAt 自己算——避免用戶端時鐘跟伺服器不同步時，同一筆資料在不同瀏覽器上算出
/// 不同的燈號。門檻見 HostHeartbeatsController：小於 2 倍回報間隔＝Online，
/// 小於 5 倍＝Delayed，否則 Offline。</summary>
public record HostHeartbeatDto(
    string Role,
    string MachineName,
    DateTimeOffset LastSeenAt,
    string Status,
    long? OutboxPending,
    double? OutboxOldestAgeSeconds,
    string? EncryptionKeyFingerprint,
    string? Channel);
