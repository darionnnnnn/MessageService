namespace MessageService.Models;

/// <summary>ApplyToAllGroups=false 時，關鍵字規則套用的群組勾選清單。</summary>
public class MaskKeywordGroup
{
    public int MaskKeywordId { get; set; }
    public MaskKeyword? MaskKeyword { get; set; }
    public required string GroupId { get; set; }
}
