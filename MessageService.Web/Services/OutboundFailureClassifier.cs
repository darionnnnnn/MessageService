using System.Net.Sockets;
using System.Security.Authentication;

namespace MessageService.Services;

/// <summary>
/// 將 outbound 請求例外分類為可直接行動的繁體中文診斷字串。
/// 純函式（無狀態、不記 log、相同輸入永遠相同輸出）。
/// </summary>
public static class OutboundFailureClassifier
{
    public static string Classify(Exception? ex, Uri? targetUri) =>
        Classify(ex, targetUri?.Host);

    public static string Classify(Exception? ex, string? hostOrUrl = null)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        var host = ExtractHost(hostOrUrl);

        // 1. 檢查是否有帶狀態碼的 HttpRequestException
        foreach (var exception in EnumerateExceptions(ex))
        {
            if (exception is HttpRequestException { StatusCode: { } statusCode })
            {
                var code = (int)statusCode;
                return code switch
                {
                    401 => "LINE 拒絕認證（401）：Line:ChannelAccessToken 無效或為空",
                    403 => "被對端拒絕（403）：經由 EdgeProxy 時請檢查 proxy 端的 EdgeProxy:AllowedClientIps 是否含這台的 IP；直連時請檢查對外連線是否被攔截",
                    429 => "被 LINE 限流（429）：稍後會自動重試",
                    404 => "目標不存在（404）：可能是 bot 已離開該群組，或 URL 路徑不正確",
                    >= 500 and <= 599 => host != null
                        ? $"HTTP {code}：{host} 對端伺服器錯誤"
                        : $"HTTP {code}：對端伺服器錯誤",
                    >= 400 and <= 499 => host != null
                        ? $"HTTP {code}：{host} 請求錯誤"
                        : $"HTTP {code}：請求錯誤",
                    _ => host != null
                        ? $"HTTP {code}：{host}"
                        : $"HTTP {code}"
                };
            }
        }

        // 2. DNS 解析失敗
        foreach (var exception in EnumerateExceptions(ex))
        {
            if (exception is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
            {
                return host != null
                    ? $"DNS 解析失敗：{host} 無法解析（防火牆或 DNS 設定）"
                    : "DNS 解析失敗：無法解析（防火牆或 DNS 設定）";
            }

            if (exception is SocketException sockEx && IsDnsError(sockEx.SocketErrorCode))
            {
                return host != null
                    ? $"DNS 解析失敗：{host} 無法解析（防火牆或 DNS 設定）"
                    : "DNS 解析失敗：無法解析（防火牆或 DNS 設定）";
            }
        }

        // 3. 連線被拒
        foreach (var exception in EnumerateExceptions(ex))
        {
            if (exception is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return host != null
                    ? $"連線被拒：{host} 拒絕連線（目標服務未啟動或防火牆 REJECT）"
                    : "連線被拒：拒絕連線（目標服務未啟動或防火牆 REJECT）";
            }
        }

        // 4. 連線逾時／無回應
        foreach (var exception in EnumerateExceptions(ex))
        {
            if (exception is TimeoutException
                or TaskCanceledException
                or SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return host != null
                    ? $"連線逾時：{host} 沒有回應（防火牆很可能未開通）"
                    : "連線逾時：沒有回應（防火牆很可能未開通）";
            }
        }

        // 5. TLS／憑證失敗
        foreach (var exception in EnumerateExceptions(ex))
        {
            if (exception is AuthenticationException
                or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            {
                return host != null
                    ? $"TLS 交握失敗：{host}"
                    : "TLS 交握失敗";
            }
        }

        // 6. 其他例外
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    private static bool IsDnsError(SocketError error) =>
        error is SocketError.HostNotFound
            or SocketError.NoData
            or SocketError.TryAgain
            or SocketError.HostUnreachable
            or SocketError.HostDown
            or SocketError.AddressNotAvailable
            or SocketError.TypeNotFound;

    private static string? ExtractHost(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
        {
            return null;
        }

        // 呼叫端傳的可能是完整 URL（頭貼下載）或純 host（其餘路徑），兩種都要能吃
        return Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) ? uri.Host : hostOrUrl.Trim();
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                foreach (var sub in EnumerateExceptions(inner))
                {
                    yield return sub;
                }
            }
        }
        else
        {
            yield return ex;
            if (ex.InnerException is not null)
            {
                foreach (var inner in EnumerateExceptions(ex.InnerException))
                {
                    yield return inner;
                }
            }
        }
    }
}
