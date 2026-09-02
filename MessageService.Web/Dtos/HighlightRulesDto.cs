namespace MessageService.Web.Dtos;

public record HighlightRulesDto(List<HighlightKeywordDto> Keywords, List<HighlightUserDto> Users);
