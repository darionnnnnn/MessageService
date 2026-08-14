namespace MessageService.Services;

/// <summary>群組摘要／成員資料是否已經過期、需要重新打一次 LINE profile API。分開查是刻意的：
/// TTL 判斷一定要在打 LINE API 之前完成才能省下配額，不能跟 upsert 合併成一次操作。</summary>
public record ProfileStaleness(bool GroupStale, bool MemberStale, string? GroupPictureFetchedUrl = null, string? MemberPictureFetchedUrl = null, bool HasGroupPicture = false, bool HasMemberPicture = false);
