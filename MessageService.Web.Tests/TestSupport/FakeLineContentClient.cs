using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeLineContentClient : ILineContentClient
{
    public List<string> ContentCalls { get; } = [];
    public List<string> TranscodingCalls { get; } = [];

    public Func<string, Task<LineContentResult>> OnGetContent { get; set; } =
        _ => Task.FromResult(new LineContentResult(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));

    public Func<string, Task<TranscodingStatus>> OnGetTranscodingStatus { get; set; } =
        _ => Task.FromResult(TranscodingStatus.Succeeded);

    public Task<LineContentResult> GetContentAsync(string messageId, CancellationToken cancellationToken)
    {
        ContentCalls.Add(messageId);
        return OnGetContent(messageId);
    }

    public Task<TranscodingStatus> GetTranscodingStatusAsync(string messageId, CancellationToken cancellationToken)
    {
        TranscodingCalls.Add(messageId);
        return OnGetTranscodingStatus(messageId);
    }

    public List<string> StickerCalls { get; } = [];
    public Func<string, Task<LineContentResult>> OnGetSticker { get; set; } =
        _ => Task.FromResult(new LineContentResult(new MemoryStream([1, 2, 3]), "image/png", 3));

    public Task<LineContentResult> GetStickerAsync(string stickerId, CancellationToken cancellationToken)
    {
        StickerCalls.Add(stickerId);
        return OnGetSticker(stickerId);
    }
}
