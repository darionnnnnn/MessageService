using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    IOptions<DeploymentOptions> deploymentOptions,
    IOptions<OutboxOptions> outboxOptions) : ControllerBase
{
    [HttpPost("poll")]
    public async Task<ActionResult<EdgePollResponse>> Poll(CancellationToken cancellationToken = default)
    {
        long? pending = null;
        double? oldestAgeSeconds = null;
        IReadOnlyList<EdgeOutboxItem> messages = [];

        if (capabilities.ReceivesWebhook)
        {
            var stats = await OutboxStatsReader.ComputeAsync(outboxDbContext, cancellationToken);
            pending = stats.OutboxPending;
            oldestAgeSeconds = stats.OutboxOldestAgeSeconds;

            var now = DateTimeOffset.UtcNow;
            messages = await outboxDbContext.Entries
                .AsNoTracking()
                .WherePending(now)
                .OrderBy(e => e.Id)
                .Take(outboxOptions.Value.BatchSize)
                .Select(e => new EdgeOutboxItem(e.WebhookEventId, e.PayloadJson))
                .ToListAsync(cancellationToken);
        }

        var response = new EdgePollResponse(
            Role: deploymentOptions.Value.Mode.ToString(),
            MachineName: Environment.MachineName,
            OutboxPending: pending,
            OutboxOldestAgeSeconds: oldestAgeSeconds,
            Messages: messages);

        return Ok(response);
    }

    [HttpPost("outbox/ack")]
    public async Task<IActionResult> AckOutbox([FromBody] EdgeOutboxAckRequest request, CancellationToken cancellationToken)
    {
        var ids = request?.WebhookEventIds;
        if (ids is null || ids.Count == 0)
        {
            return NoContent();
        }

        var maxLimit = outboxOptions.Value.BatchSize * 10;
        if (ids.Count > maxLimit)
        {
            return BadRequest($"WebhookEventIds 清單長度不得超過 {maxLimit} 筆。");
        }

        await outboxDbContext.Entries
            .Where(e => ids.Contains(e.WebhookEventId))
            .ExecuteDeleteAsync(cancellationToken);

        return NoContent();
    }
}

public record EdgePollResponse(
    string Role,
    string MachineName,
    long? OutboxPending,
    double? OutboxOldestAgeSeconds,
    IReadOnlyList<EdgeOutboxItem> Messages);

public record EdgeOutboxItem(
    string WebhookEventId,
    string PayloadJson);

public record EdgeOutboxAckRequest(
    IReadOnlyList<string> WebhookEventIds);
