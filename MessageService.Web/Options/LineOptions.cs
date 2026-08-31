namespace MessageService.Options;

/// <summary>
/// LINE outbound 外送請求的路由方式。
/// </summary>
public enum LineOutboundVia
{
    /// <summary>Direct＝Edge 自己連 internet（預設）。</summary>
    Direct,

    /// <summary>EdgeProxy＝改經 EdgeProxy 主機出去，Edge 可以完全沒有對外網路。</summary>
    EdgeProxy,
}

public class LineOptions
{
    public const string SectionName = "Line";

    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";

    /// <summary>這台主機是否要對外呼叫 LINE API（媒體下載＋頭貼快取，兩者都只需要 outbound HTTPS，
    /// 共用同一個開關）。null＝依模式推導預設值（AllInOne／Edge 為 true，Core／Viewer 為 false，
    /// 見 DeploymentCapabilities.Derive）；拆機時一對主機裡恰好一台要是 true——啟動時無法互相
    /// 檢查，設錯（兩台都真或都假）不會啟動失敗，只會變成重複下載或永遠不下載，靠部署檢查表把關，
    /// 見 docs/DEPLOYMENT-MODES.md。</summary>
    public bool? OutboundHere { get; set; }

    /// <summary>
    /// LINE 外送請求（媒體下載、貼圖下載、群組／成員資訊、頭貼下載）的路由方式。
    /// Direct＝Edge 自己連 internet（預設）；
    /// EdgeProxy＝改經 EdgeProxy 主機出去，Edge 可以完全沒有對外網路。
    /// </summary>
    public LineOutboundVia OutboundVia { get; set; } = LineOutboundVia.Direct;

    /// <summary>
    /// 當 <see cref="OutboundVia"/> 為 <see cref="LineOutboundVia.EdgeProxy"/> 時，指定 EdgeProxy 主機的基底位址
    /// （例如 http://192.0.2.10/MSLine）。
    /// Direct＝Edge 自己連 internet（預設）；
    /// EdgeProxy＝改經 EdgeProxy 主機出去，Edge 可以完全沒有對外網路。
    /// </summary>
    public string? OutboundProxyBaseUrl { get; set; }
}
