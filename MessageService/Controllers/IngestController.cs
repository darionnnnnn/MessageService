using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MessageService.Controllers;

/// <summary>Db／Full 模式的接收端點——Line 模式的 outbox 把資料轉送到這裡（見
/// HttpIngestSink）。只在設定了 Ingest:ApiKey 時存在（見 DeploymentModeConvention 對
/// RequiresIngestApiKeyAttribute 的處理），並且只受理帶正確 X-Ingest-Key 標頭、來源在
/// AllowedClientIps 白名單內的請求（見 Program.cs 掛在 /api/ingest 路徑群組的兩個中介層）。</summary>
[ApiController]
[Route("api/ingest")]
[EnabledInModes(DeploymentMode.Full, DeploymentMode.Db)]
[RequiresIngestApiKey]
public class IngestController(IIngestSink sink, ILogger<IngestController> logger) : ControllerBase
{
    [HttpPost("events")]
    public async Task<IActionResult> SubmitEvent([FromBody] IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        // IIngestSink 的契約（見介面說明）：成功（含判定為重複而略過）就正常回傳，不特別區分
        // 「新寫入」與「重複」——兩者對呼叫端（Line 端的 HttpIngestSink）而言都是「這筆已經在
        // 後端了，outbox 項目可以刪掉」，沒有必要為了純觀察用途改動這個已被測試釘住的既有契約。
        // 拋出例外＝暫時性失敗，跟本機直連時 DirectIngestSink 往外拋例外讓 forwarder 重試是
        // 同一種語意，只是隔了一層 HTTP：這裡一律回 500 讓客戶端照它自己的退避排程重試。
        try
        {
            await sink.SubmitAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to ingest webhook event {WebhookEventId}", envelope.WebhookEventId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }
}
