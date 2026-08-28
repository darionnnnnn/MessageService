namespace MessageService.Services;

/// <summary>心跳是走哪個方向進到 HostHeartbeats 的。字串而非列舉：這個值只用於顯示，
/// 存進資料庫的欄位是 nullable 字串（舊資料為 null＝未知）。</summary>
public static class HeartbeatChannel
{
    /// <summary>主機自己送來的（Edge 推送）或自己直寫的（AllInOne／Core／Viewer）。</summary>
    public const string Push = "Push";

    /// <summary>Core 主動輪詢 Edge 取回來的。</summary>
    public const string Pull = "Pull";
}
