namespace MessageService.Data.Crypto;

/// <summary>應用層欄位加密設定。收錄端與檢視端兩邊的 appsettings 必須設成完全一樣的值
/// （尤其是 Key），否則其中一端寫入的密文另一端解不開。啟用後舊資料（加密啟用前寫入的
/// 明文）仍然讀得到——FieldCipher 用 ENC1: 前綴分辨一個欄位值是不是密文，見該類別說明。</summary>
public class EncryptionOptions
{
    public const string SectionName = "Encryption";

    public bool Enabled { get; set; }

    /// <summary>base64 編碼的 32 bytes（AES-256）金鑰。產生方式：
    /// PowerShell `[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))`。</summary>
    public string? Key { get; set; }

    /// <summary>加密啟用時，訊息內容搜尋（LIKE 下推到 SQL）沒辦法對密文做子字串比對，
    /// 改成只在最近這麼多天內的文字訊息解密後在記憶體比對，見 MessagesController.Search。</summary>
    public int SearchWindowDays { get; set; } = 14;
}
