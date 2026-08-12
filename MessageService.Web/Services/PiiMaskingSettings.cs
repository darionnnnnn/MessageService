namespace MessageService.Web.Services;

/// <summary>台灣常用個資格式的內建去識別化開關，對應 ViewerSettings 的四個欄位。
/// 跟 MaskKeyword（使用者自訂關鍵字，精確比對）是互補的兩層：這裡是「長得像」某種個資格式
/// 就自動遮蔽，不需要使用者事先知道要輸入什麼關鍵字。</summary>
public record PiiMaskingSettings(bool MaskNationalId, bool MaskMobilePhone, bool MaskLandline, bool MaskNhiCard)
{
    public static readonly PiiMaskingSettings AllEnabled = new(true, true, true, true);
}
