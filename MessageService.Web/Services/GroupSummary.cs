namespace MessageService.Services;

public record GroupSummary(string GroupId, string? GroupName, string? PictureUrl, byte[]? PictureBytes = null, string? PictureContentType = null);
