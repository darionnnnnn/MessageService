using System.Net.Http.Headers;
using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Http.Features;

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
    /// <summary>轉發的目標相對路徑（開頭不帶斜線，保留 BaseAddress 的子應用程式路徑）。
    /// 入站比對用的絕對路徑由它派生，兩者永遠同一份。</summary>
    private const string ForwardPath = "api/line/webhook";
    private static readonly PathString InboundPath = new("/" + ForwardPath);

    /// <summary>request body 上限。這台在公網上、此端點無任何身分驗證（簽章是轉給 Edge 驗的），
    /// 不設上限的話任何人都能用 Kestrel 預設的 30MB body 讓這裡瞬間配置雙倍記憶體。
    /// 實際 LINE webhook 是 KB 等級，512KB 已留足餘裕；超過由 Kestrel 直接回 413，不進記憶體。</summary>
    private const long MaxWebhookBodyBytes = 512 * 1024;

    private readonly object _syncLock = new();

    /// <summary>目前是否處於「已告警的失敗狀態」。</summary>
    private bool _failing;

    /// <summary>上次記失敗 Warning 的時點。**成功不清除**——Edge 半死不活（時好時壞）時，
    /// 若每次「從成功轉失敗」都算第一次失敗而記完整堆疊，log 會以請求的頻率被灌爆；
    /// 保留這個時點讓失敗類 Warning 無論如何最多每 10 分鐘一則。</summary>
    private DateTimeOffset? _lastFailureLogAt;

    /// <summary>上次記「已恢復」Information 的時點——理由同上，flapping 時恢復訊息也要有節流上界。</summary>
    private DateTimeOffset? _lastRecoveryLogAt;

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(10);

    public async Task InvokeAsync(HttpContext context)
    {
        // 只處理 POST /api/line/webhook（含尾斜線變體——LINE Console 的 URL 人手多打一個斜線
        // 很常見，真正的 Edge 主機路由對它是寬容的，proxy 不比對就會變成「直收正常、
        // 換成 proxy 整批訊息 404」的難查差異）。其餘請求直接放行讓路由層自然回 404/405
        var path = context.Request.Path;
        if (!HttpMethods.IsPost(context.Request.Method)
            || !(path == InboundPath || path == InboundPath + "/"))
        {
            await next(context);
            return;
        }

        // 公網端點的記憶體防線：超過上限由 Kestrel 回 413，不會進到下面的緩衝
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxWebhookBodyBytes;
        }

        try
        {
            // request body 逐位元組原封轉發（維持 HMAC 驗簽有效性）
            using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, context.RequestAborted);
            var rawBody = memoryStream.ToArray();

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ForwardPath);
            var byteContent = new ByteArrayContent(rawBody);

            // 轉發標頭白名單：只轉 Content-Type 與 X-Line-Signature。Content-Type 解析不出來
            // 就不帶——把未經驗證的外部輸入原樣塞進外送標頭不是好模式，即使 Kestrel 擋得住注入
            if (context.Request.ContentType is { Length: > 0 } contentType
                && MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                byteContent.Headers.ContentType = mediaType;
            }

            if (context.Request.Headers.TryGetValue("X-Line-Signature", out var signature) && signature.Count > 0)
            {
                requestMessage.Headers.TryAddWithoutValidation("X-Line-Signature", signature.ToString());
            }

            requestMessage.Content = byteContent;

            var httpClient = httpClientFactory.CreateClient(EdgeProxyOptions.HttpClientName);
            using var response = await httpClient.SendAsync(requestMessage, context.RequestAborted);

            // 回應只透傳狀態碼，不回傳 body
            context.Response.StatusCode = (int)response.StatusCode;
            LogSuccess();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客戶端主動中斷連線，原樣往外拋給 CancelledRequestMiddleware 處理。
            // HttpClient 逾時丟的 TaskCanceledException 用的是內部 token，不會進到這個分支
            throw;
        }
        catch (Exception ex)
        {
            // 其他任何例外（連線失敗、逾時、DNS 錯誤）一律回 502 讓 LINE redelivery 接手。
            // HasStarted 防線目前不可能為真（這個中介層在 SendAsync 前不寫任何回應），純保險
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
            LogFailure(ex);
        }
    }

    private void LogSuccess()
    {
        lock (_syncLock)
        {
            if (!_failing)
            {
                return;
            }

            _failing = false;

            var now = timeProvider.GetUtcNow();
            if (_lastRecoveryLogAt is not { } last || now - last >= LogInterval)
            {
                _lastRecoveryLogAt = now;
                logger.LogInformation("轉發 webhook 至 Edge 已恢復正常。");
            }
        }
    }

    private void LogFailure(Exception ex)
    {
        lock (_syncLock)
        {
            _failing = true;

            var now = timeProvider.GetUtcNow();
            if (_lastFailureLogAt is not { } last || now - last >= LogInterval)
            {
                _lastFailureLogAt = now;
                logger.LogWarning(ex,
                    "轉發 webhook 至 Edge 失敗：{FailureReason}；這則告警（含時好時壞的情況）每 10 分鐘最多記一次。",
                    OutboundFailureClassifier.Classify(ex));
            }
        }
    }
}
