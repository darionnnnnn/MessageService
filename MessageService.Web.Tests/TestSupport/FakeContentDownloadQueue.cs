using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeContentDownloadQueue : IContentDownloadQueue
{
    public List<long> Enqueued { get; } = [];
    public List<(long MessageContentId, TimeSpan Delay)> EnqueuedDelayed { get; } = [];

    public void Enqueue(long messageContentId) => Enqueued.Add(messageContentId);

    public void EnqueueDelayed(long messageContentId, TimeSpan delay, CancellationToken cancellationToken) =>
        EnqueuedDelayed.Add((messageContentId, delay));

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used in tests.");
}
