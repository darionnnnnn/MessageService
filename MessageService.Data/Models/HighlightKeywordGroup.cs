namespace MessageService.Models;

/// <summary>ApplyToAllGroups=false 時，高亮關鍵字規則套用的群組勾選清單。</summary>
public class HighlightKeywordGroup
{
    public int HighlightKeywordId { get; set; }
    public HighlightKeyword? HighlightKeyword { get; set; }
    public string GroupId { get; set; } = string.Empty;
}
