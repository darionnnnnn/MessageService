namespace MessageService.Services;

/// <summary>POST /api/ingest/profiles/member 的請求 body。MemberProfile 本身已帶 UserId，
/// 但沒有 GroupId（它是複合鍵的一半，GroupSummary／GroupId 各自獨立於不同訊息脈絡下有意義，
/// MemberProfile 卻永遠依附在某個群組底下），這裡補上外層的 GroupId 才能定位要 upsert 哪一筆。</summary>
public record MemberUpsertRequest(string GroupId, MemberProfile Profile);
