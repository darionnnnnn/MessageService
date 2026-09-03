namespace MessageService.Web.Services;

/// <summary>某一次請求載入的遮蔽規則快照，套用邏輯全是同步、純運算（不再打 DB）。</summary>
public interface IMaskingRuleSet
{
    string MaskText(string groupId, string text);

    /// <summary>Anonymous 模式下必須帶 anonymousLabel（呼叫端已用 IAnonymousIdentityService 批次查好）。</summary>
    string ResolveDisplayName(string userId, string? rawDisplayName, string? anonymousLabel = null);

    /// <summary>是否顯示真實頭貼（僅 Original 模式）；其他模式一律回代號圖示，不能讓 PictureUrl 外流。</summary>
    bool RevealsOriginalProfile { get; }

    /// <summary>是否為完全匿名模式；為 true 時呼叫端需先用 IAnonymousIdentityService 查好代號才能組訊息。</summary>
    bool RequiresAnonymousIdentity { get; }

    /// <summary>這個使用者在目前模式下有沒有設定別名——別名是最終顯示值，
    /// 與有沒有抓到真名無關，供「名稱是否已解析」的判定使用。</summary>
    bool HasAliasFor(string userId);
}
