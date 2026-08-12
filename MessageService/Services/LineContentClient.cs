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
        // ResponseHeadersRead：不等整個 body 到齊就回傳，讓下面直接串流讀取 body，
        // 不在這裡把可達數百 MB 的影片／檔案整份讀進記憶體
        var response = await _httpClient.GetAsync(
            $"v2/bot/message/{messageId}/content", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var contentLength = response.Content.Headers.ContentLength;
            // response 要活到呼叫端讀完 stream 為止（見 LineContentResult.DisposeAsync），
            // 這裡刻意不用 using
            return new LineContentResult(stream, contentType, contentLength, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
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
