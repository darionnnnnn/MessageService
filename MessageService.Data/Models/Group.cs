namespace MessageService.Models;

public class Group
{
    public required string GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>側欄反正規化：GroupsController 改讀這兩欄，不用再對 GroupMessages 全表做
    /// GroupBy+Max。由 GroupLastMessageTracker 在訊息落地時維護；null 代表這個群組還沒有任何
    /// 訊息（理論上不會發生——Groups 列本身只在有訊息或頭貼快取寫入時才會建立）或是保留期清除
    /// 把這個群組的訊息全部清空了。</summary>
    public long? LastMessageId { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
}
