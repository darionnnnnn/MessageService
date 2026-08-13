using System.Text.Json;
using MessageService.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Outbox;

public class SqliteOutboxWriter(IServiceScopeFactory scopeFactory, IOutboxSignal signal) : IOutboxWriter
{
    // SQLITE_CONSTRAINT_UNIQUE——比對延伸錯誤碼而非只看 SqliteErrorCode==19（SQLITE_CONSTRAINT
    // 的通用主碼），NOT NULL／CHECK／FOREIGN KEY 違反也會是主碼 19，只看主碼會把那些也誤判成
    // 「已在佇列中」而靜默吞掉。體檢輪抓到這個間隙：目前唯一的呼叫端 WebhookEventHandler
    // 在建構 IngestEnvelope 前就擋掉了 WebhookEventId 為 null 的事件（見該類別），這個表也只有
    // WebhookEventId 這一欄有唯一索引，所以現狀不會誤判，但比對延伸碼才是真正對應「撞到的
    // 是哪個約束」，不依賴呼叫端有沒有做防禦性檢查
    private const int SqliteConstraintUniqueExtendedErrorCode = 2067;

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
        catch (DbUpdateException ex) when (
            ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUniqueExtendedErrorCode })
        {
            // LINE redelivery 送同一個 WebhookEventId 兩次——撞到唯一索引代表這個事件已經在
            // outbox 裡排程中，跟「寫入成功」是同一種結果，不用再叫醒 forwarder 一次
            return;
        }

        signal.NotifyNewEntry();
    }
}
