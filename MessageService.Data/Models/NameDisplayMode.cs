namespace MessageService.Models;

public enum NameDisplayMode
{
    /// <summary>顯示 LINE 原始顯示名稱。</summary>
    Original,

    /// <summary>首尾字保留、中間以 * 遮蔽。</summary>
    MaskMiddle,

    /// <summary>依 UserAliases 對照表顯示自訂別名；未設定別名者 fallback 為 MaskMiddle。</summary>
    CustomAlias,

    /// <summary>完全匿名：名稱與頭貼一律替換為該成員在該群組的動植物代號（永久指派，見 AnonymousIdentity）。</summary>
    Anonymous
}
