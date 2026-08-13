namespace MessageService.Models;

/// <summary>全站共用的單列設定（沒有登入機制，設定不分使用者）。</summary>
public class ViewerSettings
{
    public const int SingletonId = 1;

    /// <summary>與退役前 appsettings 的 Retention:Years=3 對齊，只是換了單位（年→天）。</summary>
    public const int DefaultRetentionDays = 365 * 3;

    /// <summary>保留天數的合法上限（約 10 年）。設定 API、設定頁的 input max、以及
    /// RetentionCleanupService 執行前的夾擠都以這個常數為準——保留期是不可逆的硬刪除，
    /// 三處各自寫死數字很容易改了一處忘了另外兩處。</summary>
    public const int MaxRetentionDays = 3650;

    public int Id { get; set; } = SingletonId;
    public NameDisplayMode NameDisplayMode { get; set; } = NameDisplayMode.MaskMiddle;

    /// <summary>保留天數：RetentionCleanupService 每日執行時讀取這個值決定刪除門檻。</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>台灣個資去識別化開關，預設全開；套用在 MaskingRuleSet 的內建 PII 規則管線。</summary>
    public bool MaskNationalId { get; set; } = true;
    public bool MaskMobilePhone { get; set; } = true;
    public bool MaskLandline { get; set; } = true;

    /// <summary>健保卡固定 12 碼數字的格式跟宅配貨運單號（黑貓、新竹貨運等常見業者也是 12 碼
    /// 純數字）撞在一起，預設關閉避免誤遮蔽貨運單號；群組內容以健保卡號為主的話可以在設定頁
    /// 開啟。</summary>
    public bool MaskNhiCard { get; set; } = false;
}
