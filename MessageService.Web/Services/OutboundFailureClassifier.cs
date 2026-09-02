using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace MessageService.Services;

/// <summary>
/// 把 outbound 請求的例外翻成一句可直接行動的繁體中文診斷，讓 log 一行就能分辨
/// 該查 token、對端白名單、還是防火牆。純函式（無狀態、不記 log、相同輸入永遠相同輸出）。
/// </summary>
public static class OutboundFailureClassifier
{
    /// <param name="host">實際打向的 host（走 EdgeProxy 時是 proxy 的 host，不是 LINE 的）。
    /// 取不到就傳 null，訊息會省略 host 那一段。</param>
    public static string Classify(Exception? ex, string? host = null)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        // 呼叫端手上可能只有完整 URL（例如頭貼下載），取 host 就好
        host = string.IsNullOrWhiteSpace(host)
            ? null
            : Uri.TryCreate(host.Trim(), UriKind.Absolute, out var uri) ? uri.Host : host.Trim();

        HttpStatusCode? statusCode = null;
        SocketError? socketError = null;
        HttpRequestError? requestError = null;
        var tlsFailure = false;
        var timedOut = false;
        var cancelled = false;

        // 例外鏈只走一次，把訊號收齊之後再依優先序產生訊息
        foreach (var current in EnumerateExceptions(ex))
        {
            switch (current)
            {
                case HttpRequestException httpEx:
                    statusCode ??= httpEx.StatusCode;
                    requestError ??= httpEx.HttpRequestError;
                    break;
                case SocketException socketEx:
                    socketError ??= socketEx.SocketErrorCode;
                    break;
                case AuthenticationException:
                    tlsFailure = true;
                    break;
                case TimeoutException:
                    timedOut = true;
                    break;
                case OperationCanceledException:
                    cancelled = true;
                    break;
            }
        }

        if (statusCode is { } code)
        {
            return (int)code switch
            {
                401 => "LINE 拒絕認證（401）：Line:ChannelAccessToken 無效或為空",
                403 => "被對端拒絕（403）：經由 EdgeProxy 時請檢查 proxy 端的 EdgeProxy:AllowedClientIps "
                    + "是否含這台的 IP；直連時請檢查對外連線是否被攔截",
                404 => "目標不存在（404）：可能是 bot 已離開該群組，或 URL 路徑不正確",
                429 => "被 LINE 限流（429）：稍後會自動重試",
                >= 500 and <= 599 => WithHost($"HTTP {(int)code}：{{0}}對端伺服器錯誤", host),
                >= 400 and <= 499 => WithHost($"HTTP {(int)code}：{{0}}請求錯誤", host),
                var other => WithHost($"HTTP {other}{{0}}", host is null ? null : $"：{host}"),
            };
        }

        if (requestError is HttpRequestError.NameResolutionError || socketError is SocketError.HostNotFound)
        {
            return WithCounterpart("DNS 解析失敗：{0}無法解析（防火牆或 DNS 設定）", host);
        }

        // 路由不可達與 DNS 解析失敗是兩回事：企業防火牆 DROP 掉封包時看到的是這一類，
        // 把它報成 DNS 會把排查引到完全錯誤的方向
        if (socketError is SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.HostDown or SocketError.NetworkDown)
        {
            return WithCounterpart("網路無法到達：{0}沒有路由（防火牆 DROP 或路由設定）", host);
        }

        if (socketError is SocketError.ConnectionRefused)
        {
            return WithCounterpart("連線被拒：{0}拒絕連線（目標服務未啟動或防火牆 REJECT）", host);
        }

        if (tlsFailure || requestError is HttpRequestError.SecureConnectionError)
        {
            return host is null ? "TLS 交握失敗" : $"TLS 交握失敗：{host}";
        }

        if (timedOut || socketError is SocketError.TimedOut)
        {
            return WithCounterpart("連線逾時：{0}沒有回應（防火牆很可能未開通）", host);
        }

        // 取消要跟逾時分開：HttpClient 逾時丟的 TaskCanceledException 內層帶 TimeoutException
        // （上面那條會先接住），沒有內層 TimeoutException 的就是呼叫端／使用者主動取消，
        // 報成「防火牆未開通」會誤導
        if (cancelled)
        {
            return "請求已取消（呼叫端中斷，不是連線問題）";
        }

        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>host 取不到時不要印出空括號或 null。</summary>
    private static string WithHost(string template, string? host) =>
        string.Format(template, host is null ? "" : $"{host} ");

    /// <summary>連線層失敗的模板本身沒有主詞，host 取不到時要補一個，否則會變成
    /// 「連線逾時：沒有回應」這種缺主詞的破句（實測心跳 log 出現過）。
    /// 狀態碼類的不走這條：它們的模板自己已經有主詞（「對端伺服器錯誤」），
    /// 而 <c>var other</c> 那條傳進來的是預先格式化好的「：host」字串、不是 host。</summary>
    private static string WithCounterpart(string template, string? host) =>
        WithHost(template, host ?? "對方");

    private static IEnumerable<Exception> EnumerateExceptions(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var sub in EnumerateExceptions(inner))
                {
                    yield return sub;
                }
            }

            yield break;
        }

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
