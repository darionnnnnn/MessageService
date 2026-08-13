namespace MessageService.Services;

/// <summary>這台主機自己算好的存活狀態快照，交給 IHeartbeatReporter 送出去（直接寫 DB 或
/// 打 Edge→Core 的 heartbeat 端點）。OutboxPending／OutboxOldestAgeSeconds 只有收 webhook 的
/// 主機（AllInOne／Edge）才會算，其餘主機固定傳 null——見 HeartbeatService 說明。</summary>
public record HeartbeatReport(long? OutboxPending, double? OutboxOldestAgeSeconds);
