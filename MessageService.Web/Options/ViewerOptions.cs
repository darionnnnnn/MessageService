namespace MessageService.Options;

public class ViewerOptions
{
    public const string SectionName = "Viewer";

    /// <summary>這台主機要不要開檢視端（頁面＋ /api/groups 等 API）。null＝依模式推導預設值
    /// （AllInOne／Core／Viewer 為 true，Edge 為 false）；只有三台拓撲下想讓 Core 專職資料庫、
    /// 檢視端另外開一台 Viewer 時才需要顯式設 false，見 DeploymentCapabilities.Derive。</summary>
    public bool? Enabled { get; set; }

    /// <summary>檢視端 IP 白名單，只有 IpAllowlistMiddleware 直接讀取這個設定值本身（不透過
    /// 這個類別），這裡宣告只是讓 Validate() 能檢查「檢視端開了卻沒設白名單」這種可疑組合。</summary>
    public string[] AllowedClientIps { get; set; } = [];
}
