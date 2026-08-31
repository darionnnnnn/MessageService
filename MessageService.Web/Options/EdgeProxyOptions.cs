namespace MessageService.Options;

/// <summary>
/// Deployment:Mode=EdgeProxy 專用的設定選項。
/// 只把 LINE webhook 原封轉發給 Edge 主機，本身不碰資料庫、不收錄、不持有金鑰。
/// </summary>
public class EdgeProxyOptions
{
    /// <summary>設定檔中的區段名稱。</summary>
    public const string SectionName = "EdgeProxy";

    /// <summary>轉發請求至 Edge 主機所使用的具名 HttpClient 名稱。</summary>
    public const string HttpClientName = "edge-proxy";

    /// <summary>Edge 主機的位址，例如 http://192.0.2.10/MSLine。</summary>
    public string? TargetBaseUrl { get; set; }

    /// <summary>轉發請求至 Edge 主機的逾時秒數，預設 10 秒。</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
