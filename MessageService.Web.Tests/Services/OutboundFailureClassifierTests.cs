using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using MessageService.Services;
using Xunit;

namespace MessageService.Web.Tests.Services;

public class OutboundFailureClassifierTests
{
    [Fact]
    public void Classify_Status401_ReturnsLineAuthError()
    {
        var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        var withHost = OutboundFailureClassifier.Classify(ex, "api.line.me");
        var withoutHost = OutboundFailureClassifier.Classify(ex, (string?)null);

        Assert.Equal("LINE 拒絕認證（401）：Line:ChannelAccessToken 無效或為空", withHost);
        Assert.Equal("LINE 拒絕認證（401）：Line:ChannelAccessToken 無效或為空", withoutHost);
    }

    [Fact]
    public void Classify_Status403_ReturnsForbiddenError()
    {
        var ex = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);

        var withHost = OutboundFailureClassifier.Classify(ex, "http://192.0.2.10/MSLine");
        var withoutHost = OutboundFailureClassifier.Classify(ex, (string?)null);

        Assert.Equal("被對端拒絕（403）：經由 EdgeProxy 時請檢查 proxy 端的 EdgeProxy:AllowedClientIps 是否含這台的 IP；直連時請檢查對外連線是否被攔截", withHost);
        Assert.Equal("被對端拒絕（403）：經由 EdgeProxy 時請檢查 proxy 端的 EdgeProxy:AllowedClientIps 是否含這台的 IP；直連時請檢查對外連線是否被攔截", withoutHost);
    }

    [Fact]
    public void Classify_Status429_ReturnsRateLimitError()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

        var withHost = OutboundFailureClassifier.Classify(ex, "https://api.line.me");
        var withoutHost = OutboundFailureClassifier.Classify(ex, (string?)null);

        Assert.Equal("被 LINE 限流（429）：稍後會自動重試", withHost);
        Assert.Equal("被 LINE 限流（429）：稍後會自動重試", withoutHost);
    }

    [Fact]
    public void Classify_Status404_ReturnsNotFoundError()
    {
        var ex = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);

        var withHost = OutboundFailureClassifier.Classify(ex, "api.line.me");
        var withoutHost = OutboundFailureClassifier.Classify(ex, (string?)null);

        Assert.Equal("目標不存在（404）：可能是 bot 已離開該群組，或 URL 路徑不正確", withHost);
        Assert.Equal("目標不存在（404）：可能是 bot 已離開該群組，或 URL 路徑不正確", withoutHost);
    }

    [Fact]
    public void Classify_Status5xx_ReturnsServerError()
    {
        var ex500 = new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError);
        var ex502 = new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway);

        Assert.Equal("HTTP 500：api.line.me 對端伺服器錯誤", OutboundFailureClassifier.Classify(ex500, "api.line.me"));
        Assert.Equal("HTTP 500：對端伺服器錯誤", OutboundFailureClassifier.Classify(ex500, (string?)null));
        Assert.Equal("HTTP 502：proxy.internal 對端伺服器錯誤", OutboundFailureClassifier.Classify(ex502, "proxy.internal"));
    }

    [Fact]
    public void Classify_Status4xxOther_ReturnsClientError()
    {
        var ex400 = new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest);

        Assert.Equal("HTTP 400：api.line.me 請求錯誤", OutboundFailureClassifier.Classify(ex400, "api.line.me"));
        Assert.Equal("HTTP 400：請求錯誤", OutboundFailureClassifier.Classify(ex400, (string?)null));
    }

    [Fact]
    public void Classify_DnsFailure_RealSocketException_ReturnsDnsError()
    {
        var innerSocketEx = new SocketException((int)SocketError.HostNotFound);
        var ex = new HttpRequestException("No such host is known.", innerSocketEx);

        Assert.Equal("DNS 解析失敗：api.line.me 無法解析（防火牆或 DNS 設定）", OutboundFailureClassifier.Classify(ex, "api.line.me"));
        Assert.Equal("DNS 解析失敗：無法解析（防火牆或 DNS 設定）", OutboundFailureClassifier.Classify(ex, (string?)null));
    }

    [Fact]
    public void Classify_ConnectionRefused_RealSocketException_ReturnsConnectionRefusedError()
    {
        var innerSocketEx = new SocketException((int)SocketError.ConnectionRefused);
        var ex = new HttpRequestException("No connection could be made because the target machine actively refused it.", innerSocketEx);

        Assert.Equal("連線被拒：192.0.2.10 拒絕連線（目標服務未啟動或防火牆 REJECT）", OutboundFailureClassifier.Classify(ex, "http://192.0.2.10:8080/MSLine"));
        Assert.Equal("連線被拒：拒絕連線（目標服務未啟動或防火牆 REJECT）", OutboundFailureClassifier.Classify(ex, (string?)null));
    }

    [Fact]
    public void Classify_Timeout_RealExceptions_ReturnsTimeoutError()
    {
        var timeoutEx = new HttpRequestException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());
        var canceledEx = new TaskCanceledException("The operation was canceled.", new TimeoutException());
        var socketTimedOutEx = new HttpRequestException("A connection attempt failed because the connected party did not properly respond after a period of time.", new SocketException((int)SocketError.TimedOut));

        Assert.Equal("連線逾時：api.line.me 沒有回應（防火牆很可能未開通）", OutboundFailureClassifier.Classify(timeoutEx, "api.line.me"));
        Assert.Equal("連線逾時：沒有回應（防火牆很可能未開通）", OutboundFailureClassifier.Classify(timeoutEx, (string?)null));

        Assert.Equal("連線逾時：api-data.line.me 沒有回應（防火牆很可能未開通）", OutboundFailureClassifier.Classify(canceledEx, "https://api-data.line.me/v2/bot/message/123/content"));
        Assert.Equal("連線逾時：沒有回應（防火牆很可能未開通）", OutboundFailureClassifier.Classify(canceledEx, (string?)null));

        Assert.Equal("連線逾時：api.line.me 沒有回應（防火牆很可能未開通）", OutboundFailureClassifier.Classify(socketTimedOutEx, "api.line.me"));
    }

    [Fact]
    public void Classify_TlsFailure_RealAuthenticationException_ReturnsTlsHandshakeError()
    {
        var authEx = new AuthenticationException("The remote certificate is invalid according to the validation procedure.");
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.", authEx);

        Assert.Equal("TLS 交握失敗：api.line.me", OutboundFailureClassifier.Classify(ex, "api.line.me"));
        Assert.Equal("TLS 交握失敗", OutboundFailureClassifier.Classify(ex, (string?)null));
    }

    [Fact]
    public void Classify_OtherException_ReturnsTypeNameAndMessage()
    {
        var ex = new InvalidOperationException("Something unexpected happened");

        var result = OutboundFailureClassifier.Classify(ex, "api.line.me");

        Assert.Equal("InvalidOperationException: Something unexpected happened", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_NullOrEmptyHost_DoesNotContainNullOrEmptyBrackets(string? emptyHost)
    {
        var exceptions = new Exception[]
        {
            new HttpRequestException("401", null, HttpStatusCode.Unauthorized),
            new HttpRequestException("403", null, HttpStatusCode.Forbidden),
            new HttpRequestException("429", null, HttpStatusCode.TooManyRequests),
            new HttpRequestException("404", null, HttpStatusCode.NotFound),
            new HttpRequestException("500", null, HttpStatusCode.InternalServerError),
            new HttpRequestException("400", null, HttpStatusCode.BadRequest),
            new HttpRequestException("DNS", new SocketException((int)SocketError.HostNotFound)),
            new HttpRequestException("Refused", new SocketException((int)SocketError.ConnectionRefused)),
            new HttpRequestException("Timeout", new TimeoutException()),
            new HttpRequestException("TLS", new AuthenticationException()),
            new InvalidOperationException("Other")
        };

        foreach (var ex in exceptions)
        {
            var result = OutboundFailureClassifier.Classify(ex, emptyHost);

            Assert.DoesNotContain("null", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("()", result);
            Assert.DoesNotContain("（）", result);
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void Classify_WithAndWithoutHost_OmitsHostSegmentWhenMissing()
    {
        var ex = new HttpRequestException("500", null, HttpStatusCode.InternalServerError);

        Assert.Equal("HTTP 500：api.line.me 對端伺服器錯誤", OutboundFailureClassifier.Classify(ex, "api.line.me"));
        Assert.Equal("HTTP 500：對端伺服器錯誤", OutboundFailureClassifier.Classify(ex, null));
    }

    [Fact]
    public void Classify_HostUnreachable_IsNotReportedAsDnsFailure()
    {
        // 企業防火牆 DROP 掉封包時看到的是 HostUnreachable，報成 DNS 會把排查引到錯誤方向
        var ex = new HttpRequestException("unreachable", new SocketException((int)SocketError.HostUnreachable));

        var result = OutboundFailureClassifier.Classify(ex, "api-data.line.me");

        Assert.Contains("網路無法到達", result);
        Assert.DoesNotContain("DNS", result);
    }

    [Fact]
    public void Classify_UserCancellation_IsNotReportedAsTimeout()
    {
        // HttpClient 逾時丟的 TaskCanceledException 內層帶 TimeoutException（算逾時）；
        // 沒有內層的就是呼叫端主動取消，不能報成「防火牆未開通」
        var cancelled = new TaskCanceledException("cancelled");
        var timedOut = new TaskCanceledException("timeout", new TimeoutException());

        Assert.Contains("請求已取消", OutboundFailureClassifier.Classify(cancelled, "api.line.me"));
        Assert.Contains("連線逾時", OutboundFailureClassifier.Classify(timedOut, "api.line.me"));
    }

    [Fact]
    public void Classify_AggregateException_UnwrapsProperly()
    {
        var inner = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        var agg = new AggregateException("Multiple errors", inner);

        var result = OutboundFailureClassifier.Classify(agg, "api.line.me");

        Assert.Equal("LINE 拒絕認證（401）：Line:ChannelAccessToken 無效或為空", result);
    }

    [Fact]
    public void Classify_NullException_ReturnsEmptyString()
    {
        var result = OutboundFailureClassifier.Classify(null, "api.line.me");
        Assert.Equal(string.Empty, result);
    }
}
