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

    /// <summary>預設顯示 LINE 的實際名稱與頭貼。刻意不用 MaskMiddle 當預設：名稱遮蔽的前提是
    /// 「已經取得真名，只是不想顯示」，但快取還沒回填時（新群組，或 Edge 端尚未抓到）
    /// rawDisplayName 是 null，遮蔽會退回去遮 LINE UserId 本身，畫面上變成一長串 U****…*9
    /// ——看起來像壞掉的加密字串而不是「刻意隱藏」，後台第一眼會誤判成故障。而且非 Original
    /// 模式一律不送出真實頭貼（見 MaskingRuleSet.RevealsOriginalProfile），預設遮蔽等於預設
    /// 沒有頭貼。要去識別化的部署到設定頁切換即可，見 NameDisplayMode。</summary>
    public NameDisplayMode NameDisplayMode { get; set; } = NameDisplayMode.Original;

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
