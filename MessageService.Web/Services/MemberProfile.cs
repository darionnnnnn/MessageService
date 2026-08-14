namespace MessageService.Services;

public record MemberProfile(string UserId, string? DisplayName, string? PictureUrl, byte[]? PictureBytes = null, string? PictureContentType = null);
