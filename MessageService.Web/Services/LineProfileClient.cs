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
    private readonly LineOptions _options;
    private readonly ILogger<LineProfileClient> _logger;

    public LineProfileClient(IHttpClientFactory httpClientFactory, IOptions<LineOptions> options, ILogger<LineProfileClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _httpClient.BaseAddress ??= new Uri("https://api.line.me/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.ChannelAccessToken);
            
        _imageHttpClient = httpClientFactory.CreateClient(ImageHttpClientName);
        _options = options.Value;
        _logger = logger;
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
        var (bytes, contentType) = await DownloadPictureAsync(pictureUrl, knownPictureUrl, hasPicture, cancellationToken);

        return new GroupSummary(
            root.GetProperty("groupId").GetString() ?? groupId,
            root.TryGetProperty("groupName", out var name) ? name.GetString() : null,
            pictureUrl,
            bytes,
            contentType);
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
        var (bytes, contentType) = await DownloadPictureAsync(pictureUrl, knownPictureUrl, hasPicture, cancellationToken);

        return new MemberProfile(
            root.GetProperty("userId").GetString() ?? userId,
            root.TryGetProperty("displayName", out var name) ? name.GetString() : null,
            pictureUrl,
            bytes,
            contentType);
    }
    
    private async Task<(byte[]? Bytes, string? ContentType)> DownloadPictureAsync(string? pictureUrl, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        if (pictureUrl == null || (pictureUrl == knownPictureUrl && hasPicture))
        {
            return (null, null);
        }

        try
        {
            var proxyBaseAddress = !string.IsNullOrWhiteSpace(_options.OutboundProxyBaseUrl)
                ? HttpBaseAddress.Create(_options.OutboundProxyBaseUrl)
                : null;
            var requestUrl = LineImageUrlRewriter.Rewrite(pictureUrl, _options.OutboundVia, proxyBaseAddress);

            using var response = await _imageHttpClient.GetAsync(requestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxImageSize)
            {
                _logger.LogWarning("Profile picture for {PictureUrl} is too large ({Size} bytes), skipping download", pictureUrl, contentLength);
                return (null, null);
            }
            
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxImageSize)
            {
                _logger.LogWarning("Profile picture for {PictureUrl} is too large ({Size} bytes) after reading, skipping", pictureUrl, bytes.Length);
                return (null, null);
            }
            
            return (bytes, response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download profile picture from {PictureUrl}", pictureUrl);
            return (null, null);
        }
    }
}
