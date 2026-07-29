using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeContentDownloadQueue : IContentDownloadQueue
{
    public List<long> Enqueued { get; } = [];

    public void Enqueue(long messageContentId) => Enqueued.Add(messageContentId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used in tests.");
}
