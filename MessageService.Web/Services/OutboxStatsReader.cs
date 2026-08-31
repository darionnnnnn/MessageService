using MessageService.Outbox;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>
/// 讀取 Outbox 的積壓數與最舊項目年齡（心跳統計）。
/// </summary>
public static class OutboxStatsReader
{
    public static async Task<HeartbeatReport> ComputeAsync(OutboxDbContext outboxDbContext, CancellationToken cancellationToken)
    {
        var pending = outboxDbContext.Entries.WhereDeliverable();

        var count = await pending.CountAsync(cancellationToken);
        if (count == 0)
        {
            return new HeartbeatReport(0, null);
        }

        var oldestCreatedAt = await pending.OrderBy(e => e.CreatedAt).Select(e => e.CreatedAt).FirstAsync(cancellationToken);
        return new HeartbeatReport(count, (DateTimeOffset.UtcNow - oldestCreatedAt).TotalSeconds);
    }
}
