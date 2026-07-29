using System.Threading.Channels;

namespace MessageService.Services;

public class ContentDownloadQueue : IContentDownloadQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long messageContentId) => _channel.Writer.TryWrite(messageContentId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
