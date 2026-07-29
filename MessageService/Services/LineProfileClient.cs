using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class LineProfileClient : ILineProfileClient
{
    public const string HttpClientName = "LineProfile";

    private readonly HttpClient _httpClient;

    public LineProfileClient(IHttpClientFactory httpClientFactory, IOptions<LineOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _httpClient.BaseAddress ??= new Uri("https://api.line.me/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.ChannelAccessToken);
    }

    public async Task<GroupSummary?> GetGroupSummaryAsync(string groupId, CancellationToken cancellationToken)
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

        return new GroupSummary(
            root.GetProperty("groupId").GetString() ?? groupId,
            root.TryGetProperty("groupName", out var name) ? name.GetString() : null,
            root.TryGetProperty("pictureUrl", out var picture) ? picture.GetString() : null);
    }

    public async Task<MemberProfile?> GetGroupMemberProfileAsync(string groupId, string userId, CancellationToken cancellationToken)
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

        return new MemberProfile(
            root.GetProperty("userId").GetString() ?? userId,
            root.TryGetProperty("displayName", out var name) ? name.GetString() : null,
            root.TryGetProperty("pictureUrl", out var picture) ? picture.GetString() : null);
    }
}
