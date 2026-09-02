namespace MessageService.Web.Dtos;

public record HighlightKeywordDto(int Id, string Keyword, bool ApplyToAllGroups, List<string> GroupIds);

public record UpsertHighlightKeywordDto(string Keyword, bool ApplyToAllGroups, List<string>? GroupIds);
