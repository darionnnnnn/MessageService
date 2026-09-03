namespace MessageService.Web.Dtos;

/// <param name="UnreadCount">未讀訊息數。只有 POST /api/groups/list 會依已讀基準算出實際值；
/// GET /api/groups 不帶基準，這個欄位恆為 0。</param>
public record GroupDto(
    string GroupId,
    string DisplayName,
    string? PictureUrl,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int MemberCount,
    long LastMessageId,
    int UnreadCount,
    bool NameResolved);
