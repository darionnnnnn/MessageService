namespace MessageService.Models;

/// <summary>全站共用的單列設定（沒有登入機制，設定不分使用者）。</summary>
public class ViewerSettings
{
    public const int SingletonId = 1;

    /// <summary>與退役前 appsettings 的 Retention:Years=3 對齊，只是換了單位（年→天）。</summary>
    public const int DefaultRetentionDays = 365 * 3;

    public int Id { get; set; } = SingletonId;
    public NameDisplayMode NameDisplayMode { get; set; } = NameDisplayMode.MaskMiddle;

    /// <summary>保留天數：RetentionCleanupService 每日執行時讀取這個值決定刪除門檻。</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>台灣個資去識別化開關，預設全開；套用在 MaskingRuleSet 的內建 PII 規則管線。</summary>
    public bool MaskNationalId { get; set; } = true;
    public bool MaskMobilePhone { get; set; } = true;
    public bool MaskLandline { get; set; } = true;
    public bool MaskNhiCard { get; set; } = true;
}
