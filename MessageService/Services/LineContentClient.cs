using System.Net.Http.Headers;
using System.Text.Json;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class LineContentClient : ILineContentClient
{
    public const string HttpClientName = "LineContent";

    private readonly HttpClient _httpClient;

    public LineContentClient(IHttpClientFactory httpClientFactory, IOptions<LineOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _httpClient.BaseAddress ??= new Uri("https://api-data.line.me/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.ChannelAccessToken);
    }

    public async Task<LineContentResult> GetContentAsync(string messageId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"v2/bot/message/{messageId}/content", cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        return new LineContentResult(bytes, contentType);
    }

    public async Task<TranscodingStatus> GetTranscodingStatusAsync(string messageId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"v2/bot/message/{messageId}/content/transcoding", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var status = document.RootElement.GetProperty("status").GetString();

        return status switch
        {
            "succeeded" => TranscodingStatus.Succeeded,
            "failed" => TranscodingStatus.Failed,
            _ => TranscodingStatus.Processing
        };
    }
}
