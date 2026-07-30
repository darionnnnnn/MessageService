namespace MessageService.Models;

/// <summary>
/// NameDisplayMode=Anonymous 時，某群組內某成員的動植物代號永久指派。
/// 一經指派不再變動，讓翻閱舊訊息時代號維持一致、可追溯；同群組內圖示重複時
/// 靠 Label 加序號（如「小熊 2」）區分，圖示本身仍相同、僅底色不同。
/// </summary>
public class AnonymousIdentity
{
    public required string GroupId { get; set; }
    public required string UserId { get; set; }
    public required string IconKey { get; set; }
    public required string Label { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
}
