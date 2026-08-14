namespace MessageService.Services;

public interface ILineContentClient
{
    Task<LineContentResult> GetContentAsync(string messageId, CancellationToken cancellationToken);
    Task<TranscodingStatus> GetTranscodingStatusAsync(string messageId, CancellationToken cancellationToken);
    Task<LineContentResult> GetStickerAsync(string stickerId, CancellationToken cancellationToken);
}
