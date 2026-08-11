namespace MessageService.Options;

/// <summary>
/// 決定這個部署實體扮演的角色。Full 是今天的行為（單機收 webhook＋直連資料庫），
/// Line／Db 是網段分離時的兩台拆機角色，見 docs/DEPLOYMENT-MODES.md。
/// </summary>
public enum DeploymentMode
{
    /// <summary>單機部署：收 webhook＋直連資料庫，等同過去唯一支援的形態。</summary>
    Full,

    /// <summary>只收 LINE webhook，不直連資料庫；透過 ingest API 把資料交給 Db 模式的主機。</summary>
    Line,

    /// <summary>只直連資料庫，不對外收 webhook；透過 ingest API 接收 Line 模式主機轉來的資料。</summary>
    Db
}

public class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public DeploymentMode Mode { get; set; } = DeploymentMode.Full;
}
