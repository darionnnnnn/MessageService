namespace MessageService.Web.Dtos;

public record MessagesPageDto(IReadOnlyList<MessageDto> Messages, bool HasMore, long? LatestId);
