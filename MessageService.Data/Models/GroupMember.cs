namespace MessageService.Models;

public class GroupMember
{
    public required string GroupId { get; set; }
    public required string UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public GroupMemberPicture? Picture { get; set; }
    public string? PictureContentType { get; set; }
    /// <summary>LINE 的 pictureUrl 在使用者換頭貼時會變成不同的 URL，拿它跟最新的 PictureUrl 比對就知道圖檔是不是過期了，避免每次 profile 刷新都重下載一次同一張圖。</summary>
    public string? PictureFetchedUrl { get; set; }
    public DateTimeOffset? PictureUpdatedAt { get; set; }
}
