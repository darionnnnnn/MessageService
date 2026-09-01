using System.Net.Http.Headers;
using MessageService.Web.Services;

namespace MessageService.Web.Middleware;

/// <summary>
/// Deployment:Mode=EdgeProxy 專用的 LINE outbound 轉發中介層。
/// 支援四條轉發路由：
/// 1. /line/api/{**path} -> https://api.line.me/{path}
/// 2. /line/data/{**path} -> https://api-data.line.me/{path}
/// 3. /line/sticker/{**path} -> https://stickershop.line-scdn.net/{path}
/// 4. /line/image/{host}/{**path} -> https://{host}/{path}
/// </summary>
public class EdgeProxyLineForwarder(
    RequestDelegate next,
    IHttpClientFactory httpClientFactory,
    OutboundTargetResolver resolver,
    ILogger<EdgeProxyLineForwarder> logger)
{
    public const string HttpClientName = "edge-proxy-line";

    private static readonly string[] AllowedHostSuffixes =
    [
        ".line-scdn.net",
        ".line.me"
    ];

    /// <summary>是否為純主機名：只允許英數字、點、連字號。
    /// `#`／`?`／`/`／`@`／`\`／`:` 這些在 URL 裡有特殊意義的字元一律拒絕——
    /// 它們正是讓「字尾檢查通過但實際連到別處」的繞過手法。</summary>
    private static bool IsPlainHostName(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (var c in host)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/line", out var remainingPath))
        {
            await next(context);
            return;
        }

        // 只接受 GET，其他方法不處理（放行讓路由層自然回應）
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        // 判定四條路由並組出目標絕對 URL
        string? targetUrl = null;

        if (remainingPath.StartsWithSegments("/api", out var apiRemaining))
        {
            // /line/api/{**path} -> https://api.line.me/{path}
            var subPath = apiRemaining.Value?.TrimStart('/') ?? "";
            targetUrl = $"https://api.line.me/{subPath}";
        }
        else if (remainingPath.StartsWithSegments("/data", out var dataRemaining))
        {
            // /line/data/{**path} -> https://api-data.line.me/{path}
            var subPath = dataRemaining.Value?.TrimStart('/') ?? "";
            targetUrl = $"https://api-data.line.me/{subPath}";
        }
        else if (remainingPath.StartsWithSegments("/sticker", out var stickerRemaining))
        {
            // /line/sticker/{**path} -> https://stickershop.line-scdn.net/{path}
            var subPath = stickerRemaining.Value?.TrimStart('/') ?? "";
            targetUrl = $"https://stickershop.line-scdn.net/{subPath}";
        }
        else if (remainingPath.StartsWithSegments("/image", out var imageRemaining))
        {
            // /line/image/{host}/{**path} -> https://{host}/{path}
            var rawImageSubPath = imageRemaining.Value?.TrimStart('/') ?? "";
            if (string.IsNullOrEmpty(rawImageSubPath))
            {
                await next(context);
                return;
            }

            var slashIndex = rawImageSubPath.IndexOf('/');
            string host;
            string subPath;
            if (slashIndex >= 0)
            {
                host = rawImageSubPath[..slashIndex];
                subPath = rawImageSubPath[(slashIndex + 1)..];
            }
            else
            {
                host = rawImageSubPath;
                subPath = "";
            }

            // 第四條的 {host} 必須通過寫死的網域字尾允許清單（.line-scdn.net、.line.me）才轉發，
            // 不符合一律回 403。這是「絕不做通用 proxy」在動態網域情境下的等價保證——
            // 沒有這道防線就是開放代理／SSRF。
            //
            // **只對原始字串做字尾比對是不夠的**：路徑片段會被解碼，攻擊者可以送
            // `attacker.example%23.line-scdn.net`（或 %3F、%2F），解碼後字尾檢查通過，
            // 但拼進 URL 後 `#`／`?`／`/` 會提前終止 host，實際連到的是 attacker.example。
            // 所以先夾住字元集（只允許純主機名的字元），再比對字尾，最後用解析後的
            // Uri.Host 再確認一次——三道都過才轉發
            if (!IsPlainHostName(host)
                || !AllowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("拒絕轉發未授權的 host：{Host}", host);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            targetUrl = $"https://{host}/{subPath}";

            // 最後一道：解析後的 host 必須與宣稱的完全一致，擋掉任何前面沒想到的分隔字元
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var candidate)
                || !string.Equals(candidate.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("拒絕轉發：組出的目標 host 與宣稱的不一致（{Host}）。", host);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else
        {
            // 非預期的 /line/* 路徑，放行讓後續管道處理（如 404）
            await next(context);
            return;
        }

        // query string 要一併轉發。記 log 時只留這個不含 query 的版本——LINE 的內容 URL 會把
        // 短期存取權杖放在 query 上，而失敗訊息現在會經 /proxy-admin/errors 送到 Edge 的設定頁顯示
        var targetUrlForLog = targetUrl;

        var queryString = context.Request.QueryString.Value;
        if (!string.IsNullOrEmpty(queryString))
        {
            targetUrl += queryString;
        }

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl);

            // 標頭只轉 Authorization（LINE 的 Bearer token，proxy 只是透傳、不儲存也不記錄它）。
            // 其餘標頭一律不轉。
            if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) && authHeader.Count > 0)
            {
                requestMessage.Headers.TryAddWithoutValidation("Authorization", authHeader.ToString());
            }

            var httpClient = httpClientFactory.CreateClient(HttpClientName);

            // 必須串流轉發：媒體檔可達數百 MB。
            // 與 EdgeProxyForwarderMiddleware（刻意緩衝 512KB）策略相反，理由：
            // webhook body 極小且 Edge 端需要逐位元組原封驗算 HMAC-SHA256 簽章，而 outbound 媒體檔／貼圖／頭貼
            // 可能高達數百 MB，若全讀進記憶體會迅速造成 OOM，因此必須採用 ResponseHeadersRead 串流轉發。
            using var response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            // 回應狀態碼透傳
            context.Response.StatusCode = (int)response.StatusCode;

            // Content-Type 與 Content-Length 要一併帶回（下載端要靠它們）
            if (response.Content.Headers.ContentType != null)
            {
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            }

            if (response.Content.Headers.ContentLength.HasValue)
            {
                context.Response.ContentLength = response.Content.Headers.ContentLength.Value;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await responseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客戶端中斷：原樣往外拋（交給既有的 CancelledRequestMiddleware），不要吞成 502。
            // HttpClient 逾時丟的 TaskCanceledException 用的是內部 token，不會進到這個分支
            throw;
        }
        catch (Exception ex)
        {
            // 轉發失敗（連線失敗、逾時）-> 回 502
            string resolvedTarget;
            if (targetUrlForLog is not null && Uri.TryCreate(targetUrlForLog, UriKind.Absolute, out var uri))
            {
                resolvedTarget = await resolver.ResolveAndFormatAsync(uri.Host, CancellationToken.None);
            }
            else
            {
                resolvedTarget = OutboundTargetResolver.FormatTarget(targetUrlForLog ?? string.Empty, null);
            }

            logger.LogWarning(ex, "轉發 LINE outbound 請求至 {TargetUrl} 失敗（目標 {ResolvedTarget}）", targetUrlForLog, resolvedTarget);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }
}
