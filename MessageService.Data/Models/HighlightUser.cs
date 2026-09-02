namespace MessageService.Models;

public class HighlightUser
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    /// <summary>null 代表這條規則套用到全部群組；有值代表只在該群組生效。</summary>
    public string? GroupId { get; set; }
}
