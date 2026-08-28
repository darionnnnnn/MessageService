namespace MessageService.Services;

/// <summary>
/// 記錄 Core 端收到 Edge 推送心跳（Push 通道）的最後時間。
///
/// 核心語意與日誌／震盪防護：
/// 只有 IngestController.ReportHeartbeat（推送通道）會呼叫 MarkReceived。
/// 輪詢器（EdgePullService）自己拉回來的心跳絕對不可以呼叫它——
/// 否則輪詢拉回的心跳會刷新最後到達時間，導致輪詢器誤判推送已恢復而自行停止，造成自我震盪。
/// </summary>
public class PushHeartbeatTracker(TimeProvider timeProvider)
{
    private readonly object _syncLock = new();
    private DateTimeOffset? _lastReceivedAt;

    /// <summary>
    /// 最後一次收到推送心跳的時刻（UTC）。尚未收到任何推送心跳時為 null。
    /// </summary>
    public DateTimeOffset? LastReceivedAt
    {
        get
        {
            lock (_syncLock)
            {
                return _lastReceivedAt;
            }
        }
    }

    /// <summary>
    /// 記錄現在時刻為最後收到推送心跳的時間。
    /// 僅由 IngestController.ReportHeartbeat 在成功處理推送心跳後呼叫。
    /// </summary>
    public void MarkReceived()
    {
        lock (_syncLock)
        {
            _lastReceivedAt = timeProvider.GetUtcNow();
        }
    }
}