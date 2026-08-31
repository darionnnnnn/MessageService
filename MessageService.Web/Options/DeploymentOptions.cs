namespace MessageService.Options;

/// <summary>
/// 決定這個部署實體扮演的角色，見 docs/DEPLOYMENT-MODES.md。
/// </summary>
public enum DeploymentMode
{
    /// <summary>單機部署：收 webhook＋直連資料庫＋檢視端，一台包辦。</summary>
    AllInOne,

    /// <summary>只收 LINE webhook，不直連資料庫；透過 ingest API 把資料交給 Core 模式的主機。</summary>
    Edge,

    /// <summary>只直連資料庫，不對外收 webhook；透過 ingest API 接收 Edge 模式主機轉來的資料。
    /// 預設同時開檢視端（可用 Viewer:Enabled=false 關掉，讓三台拓撲另外一台專職檢視）。</summary>
    Core,

    /// <summary>純檢視端：直連資料庫、只提供瀏覽介面，不收 webhook、不開 ingest API。
    /// 三台拓撲下與 Edge／Core 各佔一台主機時使用。</summary>
    Viewer,

    /// <summary>只把 LINE webhook 原封轉發給 Edge 主機的極簡角色：部署在有合法 HTTPS 憑證的
    /// 對外伺服器上，本身不碰資料庫、不收錄、不對外呼叫 LINE API，也不持有任何金鑰。
    /// 見 docs/DEPLOYMENT-MODES.md。</summary>
    EdgeProxy,

    // 舊名（Stage 1 之前的角色命名），與新名稱共用相同底層數值——升級時舊 appsettings.json
    // 裡的 "Full"/"Line"/"Db" 不會讓啟動失敗，讀到舊名只會記一行 Warning（見 Program.cs）。
    // 不要在新程式碼裡使用這三個名稱。
    Full = AllInOne,
    Line = Edge,
    Db = Core,
}

public class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public DeploymentMode Mode { get; set; } = DeploymentMode.AllInOne;
}
