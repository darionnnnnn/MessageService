namespace MessageService.Web.Dtos;

public record HighlightUserDto(int Id, string UserId, string? GroupId, string DisplayName, string? GroupName);

public record UpsertHighlightUserDto(string UserId, string? GroupId);
