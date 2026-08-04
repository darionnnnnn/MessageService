namespace MessageService.Web.Dtos;

public record MessageSearchResultDto(
    long MessageId,
    string GroupId,
    string GroupDisplayName,
    string DisplayName,
    string Snippet,
    DateTimeOffset EventTimestamp);
