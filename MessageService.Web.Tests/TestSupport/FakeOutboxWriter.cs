using MessageService.Outbox;
using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeOutboxWriter : IOutboxWriter
{
    public List<IngestEnvelope> Enqueued { get; } = [];

    public Task EnqueueAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        Enqueued.Add(envelope);
        return Task.CompletedTask;
    }
}
