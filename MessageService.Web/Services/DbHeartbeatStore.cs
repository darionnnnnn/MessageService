using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

public class DbHeartbeatStore(MessageDbContext dbContext) : IHeartbeatStore
{
    public async Task UpsertAsync(
        string role, string machineName, HeartbeatReport report, string? encryptionKeyFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            await ApplyUpsertAsync(role, machineName, report, encryptionKeyFingerprint, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // HostHeartbeats 主鍵為複合鍵 (Role, MachineName)。在多主機或 IIS 重疊回收（Overlapped Recycling）
            // 期間有多個行程同時回報心跳時，雙方可能同時判定列不存在而嘗試 INSERT，較慢的一方會在 SaveChangesAsync 遭遇主鍵衝突 (DbUpdateException)。
            // 此處清空 ChangeTracker 後重新查詢並改為 UPDATE 重試一次；若第二次仍失敗則往外拋出例外。其他型別例外原樣拋出。
            dbContext.ChangeTracker.Clear();
            await ApplyUpsertAsync(role, machineName, report, encryptionKeyFingerprint, cancellationToken);
        }
    }

    private async Task ApplyUpsertAsync(
        string role, string machineName, HeartbeatReport report, string? encryptionKeyFingerprint,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.HostHeartbeats
            .FirstOrDefaultAsync(h => h.Role == role && h.MachineName == machineName, cancellationToken);

        if (entity is null)
        {
            entity = new HostHeartbeat { Role = role, MachineName = machineName };
            dbContext.HostHeartbeats.Add(entity);
        }

        entity.LastSeenAt = DateTimeOffset.UtcNow;
        entity.OutboxPending = report.OutboxPending;
        entity.OutboxOldestAgeSeconds = report.OutboxOldestAgeSeconds;
        entity.EncryptionKeyFingerprint = encryptionKeyFingerprint;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
