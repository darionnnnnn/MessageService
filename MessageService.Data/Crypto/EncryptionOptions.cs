namespace MessageService.Data.Crypto;

/// <summary>應用層欄位加密設定。收錄端與檢視端兩邊的 appsettings 必須設成完全一樣的值
/// （尤其是 Key），否則其中一端寫入的密文另一端解不開。啟用後舊資料（加密啟用前寫入的
/// 明文）仍然讀得到——FieldCipher 用 ENC1: 前綴分辨一個欄位值是不是密文，見該類別說明。</summary>
public class EncryptionOptions
{
    public const string SectionName = "Encryption";

    public bool Enabled { get; set; }

    /// <summary>base64 編碼的 32 bytes（AES-256）金鑰。務必用密碼學安全亂數產生，PowerShell：
    /// <code>
    /// $bytes = New-Object byte[] 32
    /// [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    /// [Convert]::ToBase64String($bytes)
    /// </code>
    /// 不可以用 <c>Get-Random</c>——它是 System.Random（32-bit 種子的一般用途 PRNG），
    /// 產出的金鑰實際強度只有 2^32，窮舉種子即可還原，等於整套加密失效。見 docs/ENCRYPTION.md。</summary>
    public string? Key { get; set; }

    /// <summary>加密啟用時，訊息內容搜尋（LIKE 下推到 SQL）沒辦法對密文做子字串比對，
    /// 改成只在最近這麼多天內的文字訊息解密後在記憶體比對，見 MessagesController.Search。
    /// 讀取時會夾擠到 1..MaxSearchWindowDays，避免設成 0／負數導致內容搜尋永遠零筆，
    /// 或設成極大值把「解密後在記憶體比對」的成本放大到無法收拾。</summary>
    public int SearchWindowDays { get; set; } = 14;

    public const int MaxSearchWindowDays = 90;

    /// <summary>實際生效的搜尋視窗天數（已夾擠）。</summary>
    public int EffectiveSearchWindowDays => Math.Clamp(SearchWindowDays, 1, MaxSearchWindowDays);
}
