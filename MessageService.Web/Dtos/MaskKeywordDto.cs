namespace MessageService.Web.Dtos;

public record MaskKeywordDto(int Id, string Keyword, string? Replacement, bool ApplyToAllGroups, IReadOnlyList<string> GroupIds);

public record UpsertMaskKeywordDto(string Keyword, string? Replacement, bool ApplyToAllGroups, IReadOnlyList<string>? GroupIds);
