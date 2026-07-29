namespace MessageService.Models;

public class MaskKeyword
{
    public int Id { get; set; }
    public required string Keyword { get; set; }

    /// <summary>null = 預設遮蔽（等長 *）；有值 = 自訂替換字詞。</summary>
    public string? Replacement { get; set; }

    public bool ApplyToAllGroups { get; set; } = true;

    public List<MaskKeywordGroup> Groups { get; set; } = [];
}
