using MessageService.Options;

namespace MessageService.Services;

/// <summary>代寫他機心跳時的身分驗證——推送（IngestController 的 heartbeat 端點）與拉取
/// （EdgePullService）兩條通道共用同一份判斷，避免兩邊各寫一份而漂移。
///
/// Role／MachineName 直接寫進 HostHeartbeats 的主鍵欄位，設定錯誤的來源不該有辦法無上限長列。
/// 刻意不用 Enum.TryParse（會把 "99"／"-1" 這類沒有對應具名成員、只是恰好能轉成底層 int 的
/// 數字字串也判定為合法），改成直接比對宣告的成員名稱本身。</summary>
public static class HeartbeatIdentity
{
    /// <summary>HostHeartbeat.MachineName 的 HasMaxLength(128)——SQLite 不會實際擋長度，只能在這裡擋。</summary>
    public const int MaxMachineNameLength = 128;

    public static bool IsValidRole(string? role) =>
        role is not null && Enum.GetNames<DeploymentMode>().Contains(role);

    public static bool IsValidMachineName(string? machineName) =>
        !string.IsNullOrWhiteSpace(machineName) && machineName.Length <= MaxMachineNameLength;

    public static bool IsValid(string? role, string? machineName) =>
        IsValidRole(role) && IsValidMachineName(machineName);
}
