namespace MessageService.Web.Dtos;

public record UserAliasDto(string UserId, string Alias);

public record UpsertUserAliasDto(string Alias);
