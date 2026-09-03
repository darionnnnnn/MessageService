namespace MessageService.Web.Dtos;

public record MessageDto(
    long Id,
    string MessageType,
    string? Text,
    string? UserId,
    string DisplayName,
    DateTimeOffset EventTimestamp,
    MessageContentDto? Content,
    string? PictureUrl,
    string? AvatarIcon,
    string? StickerId,
    bool ProfileResolved);
