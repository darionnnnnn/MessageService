using MessageService.Data.Crypto;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class DbHeartbeatReporter(IHeartbeatStore store, IOptions<DeploymentOptions> deploymentOptions, FieldCipher cipher)
    : IHeartbeatReporter
{
    public Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken) =>
        store.UpsertAsync(deploymentOptions.Value.Mode.ToString(), Environment.MachineName, report, cipher.KeyId, cancellationToken);
}
