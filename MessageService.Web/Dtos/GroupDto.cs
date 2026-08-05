namespace MessageService.Web.Dtos;

public record GroupDto(
    string GroupId,
    string DisplayName,
    string? PictureUrl,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int MemberCount,
    long LastMessageId,
    int UnreadCount);
