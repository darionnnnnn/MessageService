namespace MessageService.Models;

public class Group
{
    public required string GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public GroupPicture? Picture { get; set; }
    public string? PictureContentType { get; set; }
    /// <summary>LINE 的 pictureUrl 在使用者換頭貼時會變成不同的 URL，拿它跟最新的 PictureUrl 比對就知道圖檔是不是過期了，避免每次 profile 刷新都重下載一次同一張圖。</summary>
    public string? PictureFetchedUrl { get; set; }
    public DateTimeOffset? PictureUpdatedAt { get; set; }

    /// <summary>LINE 官方 API 回傳的群組成員總數。為 null 代表尚未抓取或抓取失敗。</summary>
    public int? MemberCount { get; set; }

    /// <summary>側欄反正規化：GroupsController 改讀這兩欄，不用再對 GroupMessages 全表做
    /// GroupBy+Max。由 GroupLastMessageTracker 在訊息落地時維護；null 代表這個群組還沒有任何
    /// 訊息（理論上不會發生——Groups 列本身只在有訊息或頭貼快取寫入時才會建立）或是保留期清除
    /// 把這個群組的訊息全部清空了。</summary>
    public long? LastMessageId { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
}
