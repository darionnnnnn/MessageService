using MessageService.Options;

namespace MessageService.Services;

/// <summary>
/// 頭貼圖檔的 URL 改寫純函式。
/// 當 <see cref="LineOutboundVia"/> 為 <see cref="LineOutboundVia.EdgeProxy"/> 時，
/// 將符合白名單網域字尾的 LINE 頭貼 CDN 絕對 URL 改寫為經由 EdgeProxy 的轉發位址。
/// </summary>
public static class LineImageUrlRewriter
{
    // 允許改寫的目標網域字尾（寫死，不可設定）
    // ".line-scdn.net" 與 ".line.me"
    private static readonly string[] AllowedHostSuffixes =
    [
        ".line-scdn.net",
        ".line.me"
    ];

    public static string Rewrite(string originalUrl, LineOutboundVia via, Uri? proxyBaseAddress)
    {
        // via == Direct、或 proxyBaseAddress 為 null、或 originalUrl 不是合法絕對 http/https URL → 原樣回傳
        if (via == LineOutboundVia.Direct || proxyBaseAddress == null)
        {
            return originalUrl;
        }

        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return originalUrl;
        }

        // originalUrl 的 host 不符合允許清單字尾 → 原樣回傳（退回直連）。
        // 理由：LINE 換 CDN 網域時寧可退回直連，也不要靜默丟掉頭貼。
        var host = uri.Host;
        var isAllowedHost = AllowedHostSuffixes.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (!isAllowedHost)
        {
            return originalUrl;
        }

        // 回傳 {proxyBaseAddress}line/image/{原host}{原PathAndQuery}。
        // 注意 PathAndQuery 開頭已含 /，且 proxyBaseAddress 結尾若已有 /，組字串時不要多一個斜線。
        var baseAddress = proxyBaseAddress.ToString().TrimEnd('/');
        var pathAndQuery = uri.PathAndQuery; // PathAndQuery 保留 query 且以 '/' 開頭
        return $"{baseAddress}/line/image/{host}{pathAndQuery}";
    }
}
