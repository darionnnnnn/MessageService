using System.Net.Http.Headers;
using MessageService.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Middleware;

/// <summary>
/// Deployment:Mode=EdgeProxy 專用的 webhook 轉發中介層。
/// 只把 POST /api/line/webhook 原封轉發給內網的 Edge 主機，
/// 逐位元組保留 raw body 以維持 X-Line-Signature 簽章有效性。
/// 本身零狀態：不緩衝、不落地、不重試，失敗時直接回傳 502 讓 LINE redelivery 接手。
/// </summary>
public class EdgeProxyForwarderMiddleware(
    RequestDelegate next,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<EdgeProxyForwarderMiddleware> logger)
{
    private const string ForwardPath = "api/line/webhook";

    private readonly object _lock = new();
    private DateTimeOffset? _lastFailureLogAt;
    private bool _failing;

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. 只處理 POST /api/line/webhook，其餘路徑直接放行讓路由層自然回 404/405
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/api/line/webhook")
        {
            await next(context);
            return;
        }

        try
        {
            // 2. request body 逐位元組原封轉發（維持 HMAC 驗簽有效性）
            using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, context.RequestAborted);
            var rawBody = memoryStream.ToArray();

            // 4. 目標位址使用相對路徑 "api/line/webhook"（開頭不帶斜線，保留 BaseAddress 的子應用程式路徑）
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ForwardPath);
            var byteContent = new ByteArrayContent(rawBody);

            // 3. 轉發標頭白名單：只轉 Content-Type 與 X-Line-Signature
            if (context.Request.ContentType is { Length: > 0 } contentType)
            {
                if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
                {
                    byteContent.Headers.ContentType = mediaType;
                }
                else
                {
                    byteContent.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            if (context.Request.Headers.TryGetValue("X-Line-Signature", out var signature) && signature.Count > 0)
            {
                requestMessage.Headers.TryAddWithoutValidation("X-Line-Signature", signature.ToString());
            }

            requestMessage.Content = byteContent;

            var httpClient = httpClientFactory.CreateClient(EdgeProxyOptions.HttpClientName);
            using var response = await httpClient.SendAsync(requestMessage, context.RequestAborted);

            // 5. 回應只透傳狀態碼，不回傳 body
            context.Response.StatusCode = (int)response.StatusCode;
            LogSuccess();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 6. 客戶端主動中斷連線，原樣往外拋給 CancelledRequestMiddleware 處理
            throw;
        }
        catch (Exception ex)
        {
            // 6. 其他任何例外一律回傳 502 Bad Gateway，並依第 7 點節流記錄日誌
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            LogFailure(ex);
        }
    }

    private void LogSuccess()
    {
        if (!_failing)
        {
            return;
        }

        lock (_lock)
        {
            if (!_failing)
            {
                return;
            }

            _failing = false;
            _lastFailureLogAt = null;
            logger.LogInformation("轉發 webhook 至 Edge 已恢復正常。");
        }
    }

    private void LogFailure(Exception ex)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_failing)
            {
                _failing = true;
                _lastFailureLogAt = now;
                logger.LogWarning(ex, "轉發 webhook 至 Edge 失敗；持續失敗期間這則告警每 10 分鐘最多再記一次。");
                return;
            }

            if (_lastFailureLogAt is { } last && now - last >= TimeSpan.FromMinutes(10))
            {
                _lastFailureLogAt = now;
                logger.LogWarning("轉發 webhook 至 Edge 失敗（仍然失敗）：{Reason}", ex.Message);
            }
        }
    }
}
