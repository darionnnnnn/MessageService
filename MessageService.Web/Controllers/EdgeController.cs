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
    EdgeContentStaging staging,
    EdgeProfileStaging profileStaging,
    IProfileRefreshQueue profileRefreshQueue,
    IOptions<DeploymentOptions> deploymentOptions,
    IOptions<OutboxOptions> outboxOptions) : ControllerBase
{
    [HttpPost("poll")]
    public async Task<ActionResult<EdgePollResponse>> Poll(
        [FromBody] EdgePollRequest? request = null, CancellationToken cancellationToken = default)
    {
        long? pending = null;
        double? oldestAgeSeconds = null;
        IReadOnlyList<EdgeOutboxItem> messages = [];

        if (capabilities.ReceivesWebhook)
        {
            var stats = await OutboxStatsReader.ComputeAsync(outboxDbContext, cancellationToken);
            pending = stats.OutboxPending;
            oldestAgeSeconds = stats.OutboxOldestAgeSeconds;

            // 刻意不看 NextAttemptAt：那是推送方向的退避排程（見 WherePushDue）。
            // 拉取是獨立通道，被推送的退避擋住的話，Core 會有好幾分鐘看不到已經收下的訊息
            messages = await outboxDbContext.Entries
                .AsNoTracking()
                .WhereDeliverable()
                .OrderBy(e => e.Id)
                .Take(outboxOptions.Value.BatchSize)
                .Select(e => new EdgeOutboxItem(e.WebhookEventId, e.PayloadJson))
                .ToListAsync(cancellationToken);
        }

        // 收下 Core 這一輪派的媒體工作。暫存滿時只收下一部分，沒收下的留在 Core 端維持
        // Pending 下一輪再派（背壓）——回傳實際收下的清單讓 Core 知道哪些才真的認領出去了
        var acceptedWork = staging.AcceptDispatch(request?.ContentWork ?? []);

        // 名稱／頭貼刷新：Core 連同它算好的 staleness 一起派下來，入列給既有的
        // ProfileRefreshService 處理（流程不變，只是資料來源換成暫存區）
        var profileWork = request?.ProfileWork ?? [];
        if (profileWork.Count > 0)
        {
            profileStaging.Dispatch(profileWork);
            foreach (var item in profileWork)
            {
                profileRefreshQueue.Enqueue(new ProfileRefreshTask(item.GroupId, item.UserId));
            }
        }

        var response = new EdgePollResponse(
            Role: deploymentOptions.Value.Mode.ToString(),
            MachineName: Environment.MachineName,
            OutboxPending: pending,
            OutboxOldestAgeSeconds: oldestAgeSeconds,
            Messages: messages,
            AcceptedContentWork: acceptedWork,
            ReadyContentIds: staging.GetReadyIds(),
            FailedContentIds: staging.DrainFailedIds(),
            ProfileResults: profileStaging.DrainResults());

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

    /// <summary>Core 取回已下載完成的媒體內容。走獨立端點而不是塞進 poll 回應：
    /// 附檔可達數百 MB，poll 用的是短逾時的小 JSON 通道。</summary>
    [HttpGet("content/{id:long}")]
    public IActionResult GetContent(long id)
    {
        var staged = staging.Get(id);
        if (staged is null)
        {
            return NotFound();
        }

        // 明確帶 Content-Length（FileContentResult 會自動帶）——Core 端靠位元組數驗證
        // 這次取回是否完整，不完整就下一輪重取
        return File(staged.Content, staged.ContentType ?? "application/octet-stream");
    }

    /// <summary>Core 已經完整落地這筆內容，釋放記憶體暫存。
    /// 收到這個 ack 之前 Edge 不會釋放，取回中途斷掉可以原樣重取。</summary>
    [HttpPost("content/{id:long}/ack")]
    public IActionResult AckContent(long id)
    {
        staging.Release(id);
        return NoContent();
    }
}

public record EdgePollRequest(
    /// <summary>Core 這一輪要派給 Edge 下載的媒體工作。</summary>
    IReadOnlyList<ContentWorkItem> ContentWork,
    /// <summary>Core 這一輪要派給 Edge 刷新的名稱／頭貼工作（含 Core 算好的 staleness）。</summary>
    IReadOnlyList<EdgeProfileWorkItem> ProfileWork);

public record EdgePollResponse(
    string Role,
    string MachineName,
    long? OutboxPending,
    double? OutboxOldestAgeSeconds,
    IReadOnlyList<EdgeOutboxItem> Messages,
    /// <summary>本輪實際收下的媒體工作 Id——沒列在裡面的代表暫存已滿，Core 要保留為 Pending。</summary>
    IReadOnlyList<long> AcceptedContentWork,
    /// <summary>已下載完成、等 Core 用 GET content/{id} 取回的 Id。</summary>
    IReadOnlyList<long> ReadyContentIds,
    /// <summary>下載失敗、要 Core 依既有重試狀態機處理的 Id。</summary>
    IReadOnlyList<long> FailedContentIds,
    /// <summary>Edge 打完 LINE API 的名稱／頭貼結果，總量受單輪預算限制，超出的下一輪再回。</summary>
    IReadOnlyList<EdgeProfileResult> ProfileResults);

public record EdgeOutboxItem(
    string WebhookEventId,
    string PayloadJson);

public record EdgeOutboxAckRequest(
    IReadOnlyList<string> WebhookEventIds);
