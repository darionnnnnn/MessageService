namespace MessageService.Web.Services;

/// <summary>暫時實作，不做任何遮蔽。真正的規則邏輯由後續任務的 MaskingService 取代。</summary>
public class PassthroughMaskingService : IMaskingService
{
    public string MaskText(string groupId, string text) => text;

    public string ResolveDisplayName(string userId, string? rawDisplayName) => rawDisplayName ?? userId;
}
