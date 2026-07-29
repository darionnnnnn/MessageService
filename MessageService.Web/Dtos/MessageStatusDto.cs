namespace MessageService.Web.Dtos;

public record MessageStatusDto(long ContentId, string DownloadStatus, string? ContentType);
