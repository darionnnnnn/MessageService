namespace MessageService.Web.Dtos;

public record ResolvedMemberDto(
    string UserId,
    string DisplayName,
    string? PictureUrl,
    string AvatarIcon,
    bool NameResolved);
