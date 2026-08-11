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

        // 簽章合法後一律回 200：回非 2xx 會讓 LINE 重送並可能判定 webhook 失效，
        // 個別事件處理失敗只記 log（訊息遺失風險由 LINE redelivery + WebhookEventId 去重把關）
        try
        {
            var webhookRequest = JsonSerializer.Deserialize<WebhookRequest>(rawBody);
            if (webhookRequest is not null)
            {
                await eventHandler.HandleAsync(webhookRequest, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to process webhook request body");
        }

        return Ok();
    }
}
