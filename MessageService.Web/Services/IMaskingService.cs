namespace MessageService.Web.Services;

/// <summary>套用關鍵字遮蔽與名稱顯示模式。實作於獨立任務中補上；此為 Messages API 提前定義的介面契約。</summary>
public interface IMaskingService
{
    string MaskText(string groupId, string text);
    string ResolveDisplayName(string userId, string? rawDisplayName);
}
