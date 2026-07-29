namespace MessageService.Models;

public enum NameDisplayMode
{
    /// <summary>顯示 LINE 原始顯示名稱。</summary>
    Original,

    /// <summary>首尾字保留、中間以 * 遮蔽。</summary>
    MaskMiddle,

    /// <summary>依 UserAliases 對照表顯示自訂別名；未設定別名者 fallback 為 MaskMiddle。</summary>
    CustomAlias
}
