using System.Text.Json;
using MessageService.Models.Line;
using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MessageService.Controllers;

// Db 模式的主機不收 webhook（它只透過 ingest API 接收 Line 模式主機轉來的資料）——見
// DeploymentModeConvention，這裡宣告的模式集合就是路由存在與否的唯一依據
[ApiController]
[Route("api/line/webhook")]
[EnabledInModes(DeploymentMode.Full, DeploymentMode.Line)]
public class LineWebhookController(
    ILineSignatureValidator signatureValidator,
    IWebhookEventHandler eventHandler,
    ILogger<LineWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        await Request.Body.CopyToAsync(memoryStream, cancellationToken);
        var rawBody = memoryStream.ToArray();

        var signature = Request.Headers["X-Line-Signature"].ToString();
        if (!signatureValidator.IsValid(rawBody, signature))
        {
            logger.LogWarning("Rejected webhook request with invalid signature");
            return Unauthorized();
        }

        WebhookRequest? webhookRequest;
        try
        {
            webhookRequest = JsonSerializer.Deserialize<WebhookRequest>(rawBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 格式不合的 payload 重送也不會變好，回 200 避免 LINE 判定 webhook 失效而無限重試
            logger.LogError(ex, "Failed to parse webhook request body");
            return Ok();
        }

        if (webhookRequest is null)
        {
            return Ok();
        }

        try
        {
            await eventHandler.HandleAsync(webhookRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 這是唯一會讓事件真的遺失的失敗（本機 outbox 寫不進去：磁碟滿／DB 鎖住／損毀）——
            // 回 500 讓 LINE redelivery 接手重試；重送造成的重複由落地端 WebhookEventId
            // 唯一索引擋掉，見 DirectIngestSink，安全
            logger.LogError(ex, "Failed to enqueue webhook events to outbox");
            return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }
}
