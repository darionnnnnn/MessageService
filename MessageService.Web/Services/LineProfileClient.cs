using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MessageService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class LineProfileClient : ILineProfileClient
{
    public const string HttpClientName = "LineProfile";
    public const string ImageHttpClientName = "LineProfileImage";
    public const int MaxImageSize = 2 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _imageHttpClient;
    private readonly IOptionsMonitor<LineOptions> _optionsMonitor;
    private readonly ILogger<LineProfileClient> _logger;

    public LineProfileClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<LineOptions> optionsMonitor, ILogger<LineProfileClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _httpClient.BaseAddress ??= new Uri("https://api.line.me/");
            
        _imageHttpClient = httpClientFactory.CreateClient(ImageHttpClientName);
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    /// <summary>頭貼網域不在改寫清單時的告警節流時點——每次刷新都會走到，不節流會刷爆 log。</summary>
    private static DateTimeOffset? _lastUnrewritableWarningAt;
    private static readonly object _warnLock = new();

    private void WarnUnrewritableImageHost(string pictureUrl)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_warnLock)
        {
            if (_lastUnrewritableWarningAt is { } last && now - last < TimeSpan.FromMinutes(10))
            {
                return;
            }
            _lastUnrewritableWarningAt = now;
        }

        _logger.LogWarning(
            "Line:OutboundVia=EdgeProxy，但頭貼網址 {PictureUrl} 的網域不在改寫允許清單內，" +
            "這次退回直連。若這台沒有對外網路，頭貼會下載失敗——請確認 LINE 是否更換了 CDN 網域。",
            pictureUrl);
    }

    public async Task<GroupSummary?> GetGroupSummaryAsync(string groupId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"v2/bot/group/{groupId}/summary", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var pictureUrl = root.TryGetProperty("pictureUrl", out var picture) ? picture.GetString() : null;
        var pictureFetch = await DownloadPictureAsync(pictureUrl, knownPictureUrl, hasPicture, cancellationToken);
        var memberCount = await GetGroupMemberCountAsync(groupId, cancellationToken);

        return new GroupSummary(
            root.GetProperty("groupId").GetString() ?? groupId,
            root.TryGetProperty("groupName", out var name) ? name.GetString() : null,
            pictureUrl,
            pictureFetch.Bytes,
            pictureFetch.ContentType,
            pictureFetch.TransientFailure,
            pictureFetch.PermanentlyUnavailable,
            memberCount);
    }

    /// <summary>
    /// 取得群組真實成員總數（GET v2/bot/group/{groupId}/members/count）。
    /// 失敗時不拋出例外，回傳 null 以避免影響整個群組 profile 的刷新。
    /// </summary>
    public async Task<int?> GetGroupMemberCountAsync(string groupId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"v2/bot/group/{groupId}/members/count", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get member count for group {GroupId}: HTTP {StatusCode}", groupId, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var count))
            {
                return count;
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 只有呼叫端真的取消才往外丟；HttpClient 逾時同樣是 TaskCanceledException，
            // 無條件重拋會讓一次逾時把整個群組刷新（含名稱）一起中斷
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get member count for group {GroupId}", groupId);
            return null;
        }
    }

    public async Task<MemberProfile?> GetGroupMemberProfileAsync(string groupId, string userId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"v2/bot/group/{groupId}/member/{userId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var pictureUrl = root.TryGetProperty("pictureUrl", out var picture) ? picture.GetString() : null;
        var pictureFetch = await DownloadPictureAsync(pictureUrl, knownPictureUrl, hasPicture, cancellationToken);

        return new MemberProfile(
            root.GetProperty("userId").GetString() ?? userId,
            root.TryGetProperty("displayName", out var name) ? name.GetString() : null,
            pictureUrl,
            pictureFetch.Bytes,
            pictureFetch.ContentType,
            pictureFetch.TransientFailure,
            pictureFetch.PermanentlyUnavailable);
    }
    
    /// <summary>頭貼下載結果。暫時性與永久性失敗要分開：staleness 把「有網址卻沒有圖」
    /// 判定為過期，永久性失敗若也走重試路徑，同一張拿不到的圖會被無限期地每 10 分鐘重抓一次。</summary>
    private readonly record struct PictureFetch(
        byte[]? Bytes, string? ContentType, bool TransientFailure, bool PermanentlyUnavailable)
    {
        public static readonly PictureFetch NotAttempted = new(null, null, false, false);
        public static PictureFetch Downloaded(byte[] bytes, string? contentType) => new(bytes, contentType, false, false);
        public static readonly PictureFetch Transient = new(null, null, true, false);
        public static readonly PictureFetch Permanent = new(null, null, false, true);
    }

    private async Task<PictureFetch> DownloadPictureAsync(string? pictureUrl, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        if (pictureUrl == null || (pictureUrl == knownPictureUrl && hasPicture))
        {
            return PictureFetch.NotAttempted;
        }

        string? requestUrl = null;
        try
        {
            var options = _optionsMonitor.CurrentValue;
            var proxyBaseAddress = !string.IsNullOrWhiteSpace(options.OutboundProxyBaseUrl)
                ? HttpBaseAddress.Create(options.OutboundProxyBaseUrl)
                : null;
            requestUrl = LineImageUrlRewriter.Rewrite(pictureUrl, options.OutboundVia, proxyBaseAddress);

            // 設定要走 proxy、URL 卻沒被改寫，代表 LINE 換了頭貼 CDN 的網域、不在改寫器的
            // 允許清單裡。這時會退回直連（不丟掉頭貼），但 Edge 沒有對外網路的部署會下載失敗——
            // 沒有這行的話症狀只會是「頭貼一直空白」，查不到原因。每 10 分鐘最多記一次
            if (options.OutboundVia == LineOutboundVia.EdgeProxy
                && ReferenceEquals(requestUrl, pictureUrl))
            {
                WarnUnrewritableImageHost(pictureUrl);
            }

            using var response = await _imageHttpClient.GetAsync(requestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxImageSize)
            {
                // 超過上限是永久性的：回報成暫時失敗會讓同一張過大的圖被無限期重抓
                _logger.LogWarning("Profile picture for {PictureUrl} is too large ({Size} bytes), skipping download", pictureUrl, contentLength);
                return PictureFetch.Permanent;
            }
            
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxImageSize)
            {
                _logger.LogWarning("Profile picture for {PictureUrl} is too large ({Size} bytes) after reading, skipping", pictureUrl, bytes.Length);
                return PictureFetch.Permanent;
            }
            
            return PictureFetch.Downloaded(bytes, response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 同上：HttpClient 逾時丟的 TaskCanceledException 也是 OperationCanceledException 的子類，
            // 判斷依據一律看取消權杖，否則逾時會被當成「呼叫端取消」而不是暫時性下載失敗
            throw;
        }
        catch (Exception ex)
        {
            var targetHost = requestUrl ?? pictureUrl;
            _logger.LogWarning(ex, "Failed to download profile picture from {PictureUrl}: {FailureReason}",
                pictureUrl, OutboundFailureClassifier.Classify(ex, targetHost));

            // 404／410 代表這個網址本身沒東西，重試多少次都一樣；
            // 緩衝溢位代表圖片超過上限，亦為永久失敗，避免無限重抓。
            var permanent = IsPermanentPictureDownloadFailure(ex);
            return permanent ? PictureFetch.Permanent : PictureFetch.Transient;
        }
    }

    /// <summary>
    /// 判定頭貼下載失敗是否屬於永久性失敗（Permanent）：
    /// 1. HTTP 404（NotFound）或 410（Gone）：遠端資源不存在。
    /// 2. 緩衝溢位例外（HttpRequestError.ConfigurationLimitExceeded）：
    ///    圖片實體大小超過 MaxResponseContentBufferSize，屬於過大圖片。
    /// 一般網路錯誤（如逾時 TimeoutException、連線被拒 SocketError.ConnectionRefused、DNS 解析失敗、5xx 伺服器錯誤等）
    /// 絕不判為永久失敗，維持 TransientFailure，保留重試機會。
    /// </summary>
    private static bool IsPermanentPictureDownloadFailure(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return true;
            }

            if (httpEx.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                return true;
            }
        }

        return false;
    }
}
