namespace MessageService.Web.Dtos;

public record MessageContentDto(long Id, string? FileName, string? ContentType, string DownloadStatus);
