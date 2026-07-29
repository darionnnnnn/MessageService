namespace MessageService.Web.Services;

/// <summary>某一次請求載入的遮蔽規則快照，套用邏輯全是同步、純運算（不再打 DB）。</summary>
public interface IMaskingRuleSet
{
    string MaskText(string groupId, string text);
    string ResolveDisplayName(string userId, string? rawDisplayName);
}
