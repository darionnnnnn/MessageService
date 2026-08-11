using System.Text.Json;
using MessageService.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Outbox;

public class SqliteOutboxWriter(IServiceScopeFactory scopeFactory, IOutboxSignal signal) : IOutboxWriter
{
    public async Task EnqueueAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        dbContext.Entries.Add(new OutboxEntry
        {
            WebhookEventId = envelope.WebhookEventId,
            PayloadJson = JsonSerializer.Serialize(envelope),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        signal.NotifyNewEntry();
    }
}
