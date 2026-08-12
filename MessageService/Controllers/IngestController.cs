using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MessageService.Controllers;

/// <summary>Db／Full 模式的接收端點——Line 模式的 outbox 把資料轉送到這裡（見
/// HttpIngestSink），媒體下載與頭貼快取（見 ApiContentWorkSource／ApiProfileStore）也走
/// 這裡。只在設定了 Ingest:ApiKey 時存在（見 DeploymentModeConvention 對
/// RequiresIngestApiKeyAttribute 的處理），並且只受理帶正確 X-Ingest-Key 標頭、來源在
/// AllowedClientIps 白名單內的請求（見 Program.cs 掛在 /api/ingest 路徑群組的兩個中介層）。</summary>
[ApiController]
[Route("api/ingest")]
[EnabledInModes(DeploymentMode.Full, DeploymentMode.Db)]
[RequiresIngestApiKey]
public class IngestController(
    IIngestSink sink,
    IContentWorkSource contentWorkSource,
    IProfileStore profileStore,
    IContentDownloadQueue downloadQueue,
    IProfileRefreshQueue profileRefreshQueue,
    IOptions<IngestOptions> ingestOptions,
    ILogger<IngestController> logger) : ControllerBase
{
    [HttpPost("events")]
    public async Task<IActionResult> SubmitEvent([FromBody] IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        // IIngestSink 的契約（見介面說明）：成功（含判定為重複而略過）就正常回傳，不特別區分
        // 「新寫入」與「重複」——兩者對呼叫端（Line 端的 HttpIngestSink）而言都是「這筆已經在
        // 後端了，outbox 項目可以刪掉」，沒有必要為了純觀察用途改動這個已被測試釘住的既有契約。
        // 但 ContentId 兩種情況都要回傳（見 IngestResult 說明）——這是拆機模式的媒體下載
        // 唯一知道要下載哪一筆的管道，跟「重複判定」不是同一類問題，不適用同一套「不值得
        // 打破契約」的理由。
        // 拋出例外＝暫時性失敗，跟本機直連時 DirectIngestSink 往外拋例外讓 forwarder 重試是
        // 同一種語意，只是隔了一層 HTTP：這裡一律回 500 讓客戶端照它自己的退避排程重試。
        IngestResult result;
        try
        {
            result = await sink.SubmitAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to ingest webhook event {WebhookEventId}", envelope.WebhookEventId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        // 這台主機自己要不要接手後續處理，取決於它自己的 IContentDownloadQueue／
        // IProfileRefreshQueue 是真的還是 Null（見 IngestSideEffects 說明）——
        // 這支端點通常在 Db:OutboundHere=false 的部署上是無操作，媒體交給 Line 端處理
        IngestSideEffects.Apply(envelope, result, downloadQueue, profileRefreshQueue);

        return Ok(new IngestEventResponse(result.ContentId));
    }

    // === 媒體下載（Line:OutboundHere=true 時，Line 端的 ApiContentWorkSource 打這幾支） ===

    [HttpGet("content-work")]
    public async Task<ActionResult<IReadOnlyList<long>>> GetContentWork(CancellationToken cancellationToken) =>
        Ok(await contentWorkSource.GetPendingIdsAsync(cancellationToken));

    [HttpGet("content-work/{id:long}")]
    public async Task<ActionResult<ContentWorkItem>> GetContentWorkItem(long id, CancellationToken cancellationToken)
    {
        var item = await contentWorkSource.GetAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPut("content/{id:long}")]
    [RequestSizeLimit(long.MaxValue)] // 真正的上限用 IHttpMaxRequestBodySizeFeature 動態套 Ingest:MaxContentBytes（見下）
    public async Task<IActionResult> UploadContent(long id, CancellationToken cancellationToken)
    {
        // Kestrel 預設請求主體上限 30MB 擋得住 LINE 的大型影片／檔案；Ingest:MaxContentBytes
        // 是設定值不是編譯期常數，沒辦法用 [RequestSizeLimit] 套，必須在讀取 body 之前
        // 透過這個 feature 動態調整。IsReadOnly 在某些代管環境（例如 IIS in-process）下可能
        // 是 true，那種情況就沿用伺服器層級的預設值，不讓這裡例外中斷請求
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = ingestOptions.Value.MaxContentBytes;
        }

        using var memoryStream = new MemoryStream();
        await Request.Body.CopyToAsync(memoryStream, cancellationToken);

        await contentWorkSource.CompleteAsync(id, memoryStream.ToArray(), Request.ContentType, cancellationToken);
        return NoContent();
    }

    [HttpPost("content/{id:long}/failed")]
    public async Task<IActionResult> MarkContentFailed(long id, CancellationToken cancellationToken)
    {
        await contentWorkSource.FailAsync(id, cancellationToken);
        return NoContent();
    }

    // === 頭貼快取（Line:OutboundHere=true 時，Line 端的 ApiProfileStore 打這幾支） ===

    [HttpGet("profiles/staleness")]
    public async Task<ActionResult<ProfileStaleness>> GetProfileStaleness(
        [FromQuery] string groupId, [FromQuery] string? userId, [FromQuery] DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        Ok(await profileStore.GetStalenessAsync(groupId, userId, cutoff, cancellationToken));

    [HttpPost("profiles/group")]
    public async Task<IActionResult> UpsertGroupProfile([FromBody] GroupSummary summary, CancellationToken cancellationToken)
    {
        await profileStore.UpsertGroupAsync(summary.GroupId, summary, cancellationToken);
        return NoContent();
    }

    [HttpPost("profiles/member")]
    public async Task<IActionResult> UpsertMemberProfile([FromBody] MemberUpsertRequest request, CancellationToken cancellationToken)
    {
        await profileStore.UpsertMemberAsync(request.GroupId, request.Profile.UserId, request.Profile, cancellationToken);
        return NoContent();
    }
}
