namespace MessageService.Models;

public class HighlightKeyword
{
    public int Id { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public bool ApplyToAllGroups { get; set; } = true;
    public List<HighlightKeywordGroup> Groups { get; set; } = [];
}