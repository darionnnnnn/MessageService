using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeProfileRefreshQueue : IProfileRefreshQueue
{
    public List<ProfileRefreshTask> Enqueued { get; } = [];

    public void Enqueue(ProfileRefreshTask task) => Enqueued.Add(task);

    public IAsyncEnumerable<ProfileRefreshTask> ReadAllAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used in tests.");
}
