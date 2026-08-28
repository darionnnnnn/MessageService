using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MessageService.Controllers;

/// <summary>
/// Edge 模式的拉取 API 面（由 Core 端主機定期輪詢）。
/// </summary>
[ApiController]
[Route("api/edge")]
[RequiresCapability(Capability.EdgePullApi)]
public class EdgeController(
    OutboxDbContext outboxDbContext,
    DeploymentCapabilities capabilities,
    IOptions<DeploymentOptions> deploymentOptions) : ControllerBase
{
    [HttpPost("poll")]
    public async Task<ActionResult<EdgePollResponse>> Poll(CancellationToken cancellationToken = default)
    {
        long? pending = null;
        double? oldestAgeSeconds = null;

        if (capabilities.ReceivesWebhook)
        {
            var stats = await OutboxStatsReader.ComputeAsync(outboxDbContext, cancellationToken);
            pending = stats.OutboxPending;
            oldestAgeSeconds = stats.OutboxOldestAgeSeconds;
        }

        var response = new EdgePollResponse(
            Role: deploymentOptions.Value.Mode.ToString(),
            MachineName: Environment.MachineName,
            OutboxPending: pending,
            OutboxOldestAgeSeconds: oldestAgeSeconds);

        return Ok(response);
    }
}

public record EdgePollResponse(
    string Role,
    string MachineName,
    long? OutboxPending,
    double? OutboxOldestAgeSeconds);
