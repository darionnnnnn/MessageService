namespace MessageService.Options;

/// <summary>
/// Edge 與 Core 之間的傳輸通道模式。
/// </summary>
public enum IngestChannel
{
    /// <summary>推送優先，同時開放拉取 API 面（預設）。</summary>
    Auto,

    /// <summary>Edge 只主動推送給 Core，不開放拉取 API 面。</summary>
    Push,

    /// <summary>Edge 從不主動連 Core，只開放拉取 API 面供 Core 輪詢。</summary>
    Pull,
}

/// <summary>
/// Ingest API 的雙邊設定：Line 模式用 BaseUrl／ApiKey 當客戶端打去 Db 模式主機；
/// Db 模式用 ApiKey 驗證進來的請求（同一把金鑰，兩邊都要設成一樣的值）。
/// </summary>
public class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// Edge 與 Core 之間的傳輸通道模式：
    /// Auto（預設，推送優先且開放拉取）、Push（只主動推送）、Pull（不主動連 Core，只開放拉取）。
    /// </summary>
    public IngestChannel Channel { get; set; } = IngestChannel.Auto;

    /// <summary>Line 模式專用：Db 模式主機的 ingest API 位址（如 https://db-host/）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>共用密鑰：Line 模式當請求標頭送出、Db 模式驗證進來的請求，兩邊必須一致。</summary>
    public string? ApiKey { get; set; }

    /// <summary>PUT /api/ingest/content/{id} 單次上傳允許的最大位元組數。Kestrel 預設請求主體
    /// 上限是 30MB，擋得住 LINE 的大型影片／檔案，這裡放寬（見 IngestController 如何套用）。
    /// 預設 300MB，實際要多大依部署會經手的最大檔案而定，寫進部署文件。
    /// IIS 部署時 web.config 的 requestLimits maxAllowedContentLength（同為 300MB）會先擋——
    /// 改這裡的值要同步改 MessageService.Web/web.config 那一處，反之亦然。</summary>
    public long MaxContentBytes { get; set; } = 300L * 1024 * 1024;

    /// <summary>Core 端專用：Edge 主機的位址。空＝永不輪詢（與現行行為完全相同）。</summary>
    public string? EdgeBaseUrl { get; set; }

    /// <summary>輪詢間隔（秒）。預設 1 秒。</summary>
    public int PullIntervalSeconds { get; set; } = 1;

    /// <summary>多久沒收到「推送」心跳才啟動輪詢（秒）。預設 180 秒。</summary>
    public int PullActivationSeconds { get; set; } = 180;

    /// <summary>poll 連續失敗時的退避上限（秒）。預設 60 秒。</summary>
    public int PullFailureMaxBackoffSeconds { get; set; } = 60;

    /// <summary>Edge 端 Auto 模式專用：推送失敗後，每隔多久放行一次推送當作探測（分鐘）。
    /// 預設 60 分。探測期間 Core 端輪詢照常取走資料，這個週期只影響「防火牆重新開通後
    /// 多久自動升級回推送」，調短不會提升可靠度。</summary>
    public int ChannelProbeIntervalMinutes { get; set; } = 60;
}
