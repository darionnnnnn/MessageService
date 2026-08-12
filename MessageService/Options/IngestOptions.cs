namespace MessageService.Options;

/// <summary>
/// Ingest API 的雙邊設定：Line 模式用 BaseUrl／ApiKey 當客戶端打去 Db 模式主機；
/// Db 模式用 ApiKey 驗證進來的請求（同一把金鑰，兩邊都要設成一樣的值）。
/// </summary>
public class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>Line 模式專用：Db 模式主機的 ingest API 位址（如 https://db-host/）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>共用密鑰：Line 模式當請求標頭送出、Db 模式驗證進來的請求，兩邊必須一致。</summary>
    public string? ApiKey { get; set; }

    /// <summary>PUT /api/ingest/content/{id} 單次上傳允許的最大位元組數。Kestrel 預設請求主體
    /// 上限是 30MB，擋得住 LINE 的大型影片／檔案，這裡放寬（見 IngestController 如何套用）。
    /// 預設 300MB，實際要多大依部署會經手的最大檔案而定，寫進部署文件。</summary>
    public long MaxContentBytes { get; set; } = 300L * 1024 * 1024;
}
