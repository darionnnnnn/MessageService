using System.Text.Json;
using MessageService.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Outbox;

public class SqliteOutboxWriter(IServiceScopeFactory scopeFactory, IOutboxSignal signal) : IOutboxWriter
{
    private const int SqliteConstraintErrorCode = 19; // SQLITE_CONSTRAINT

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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
        {
            // LINE redelivery 送同一個 WebhookEventId 兩次——撞到唯一索引代表這個事件已經在
            // outbox 裡排程中，跟「寫入成功」是同一種結果，不用再叫醒 forwarder 一次
            return;
        }

        signal.NotifyNewEntry();
    }
}
