using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeHeartbeatStore : IHeartbeatStore
{
    public List<(string Role, string MachineName, HeartbeatReport Report, string? EncryptionKeyFingerprint, string Channel)> Upserted { get; } = [];

    public Task UpsertAsync(
        string role, string machineName, HeartbeatReport report, string? encryptionKeyFingerprint,
        string channel, CancellationToken cancellationToken)
    {
        Upserted.Add((role, machineName, report, encryptionKeyFingerprint, channel));
        return Task.CompletedTask;
    }
}
