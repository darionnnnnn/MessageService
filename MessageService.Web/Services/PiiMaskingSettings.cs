using MessageService.Models;

namespace MessageService.Web.Services;

/// <summary>台灣常用個資格式的內建去識別化開關，對應 ViewerSettings 的四個欄位。
/// 跟 MaskKeyword（使用者自訂關鍵字，精確比對）是互補的兩層：這裡是「長得像」某種個資格式
/// 就自動遮蔽，不需要使用者事先知道要輸入什麼關鍵字。</summary>
public record PiiMaskingSettings(bool MaskNationalId, bool MaskMobilePhone, bool MaskLandline, bool MaskNhiCard)
{
    /// <summary>測試用的「四種格式全開」組合——正式程式碼的後備一律用 Defaults，不要用這個
    /// （健保卡預設已改關閉，AllEnabled 不再等於預設值）。</summary>
    public static readonly PiiMaskingSettings AllEnabled = new(true, true, true, true);

    /// <summary>ViewerSettings 類別預設值的投影——singleton 設定列不存在、或 MaskingRuleSet
    /// 建構子沒帶 pii 設定時的後備，跟 migration 種子同一個定義點。不要在任何地方另外硬寫
    /// 一份布林組合：健保卡預設改關那輪，散落的「第二份預設」就漂移了三處。</summary>
    public static PiiMaskingSettings Defaults { get; } = FromViewerSettings(new ViewerSettings());

    public static PiiMaskingSettings FromViewerSettings(ViewerSettings settings) =>
        new(settings.MaskNationalId, settings.MaskMobilePhone, settings.MaskLandline, settings.MaskNhiCard);
}
