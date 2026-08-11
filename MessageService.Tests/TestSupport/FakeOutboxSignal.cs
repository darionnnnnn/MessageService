using MessageService.Outbox;

namespace MessageService.Tests.TestSupport;

public class FakeOutboxSignal : IOutboxSignal
{
    public int NotifyCount { get; private set; }

    public void NotifyNewEntry() => NotifyCount++;

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used in tests.");
}
