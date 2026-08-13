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
