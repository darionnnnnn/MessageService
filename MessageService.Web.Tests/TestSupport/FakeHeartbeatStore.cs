using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeHeartbeatStore : IHeartbeatStore
{
    public List<(string Role, string MachineName, HeartbeatReport Report, string? EncryptionKeyFingerprint)> Upserted { get; } = [];

    public Task UpsertAsync(
        string role, string machineName, HeartbeatReport report, string? encryptionKeyFingerprint,
        CancellationToken cancellationToken)
    {
        Upserted.Add((role, machineName, report, encryptionKeyFingerprint));
        return Task.CompletedTask;
    }
}
