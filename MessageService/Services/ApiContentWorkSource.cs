using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MessageService.Services;

/// <summary>Line 模式且 Line:OutboundHere=true 用：打 Db 端主機的 ingest API 取得待辦、
/// 回報結果，取代 DbContentWorkSource 直接查資料庫的角色。用兩個具名 HttpClient——
/// "ingest"（短 timeout，取清單／單筆詳情／標記失敗都是小型 JSON）與 "ingest-content"
/// （長 timeout，PUT 上傳 blob 可能是數百 MB）。任何非 2xx 或連線層錯誤一律往外拋，
/// 交給 ContentDownloadService 既有的重試／Failed 標記邏輯處理，這裡不用特別分辨
/// 「永久失敗」——跟 IIngestSink／outbox 的場景不同，media 下載本來就已經有一套
/// MaxRetries／Failed 狀態機，不需要疊加第二套死信機制。
///
/// HttpClient 不 Dispose：IHttpClientFactory.CreateClient() 回傳的實例本來就是設計成
/// 用完即棄，底層 handler 由工廠集中管理生命週期，重複 CreateClient 不會造成 socket 耗盡
/// （這正是 IHttpClientFactory 存在的理由）。</summary>
public class ApiContentWorkSource(IHttpClientFactory httpClientFactory) : IContentWorkSource
{
    private HttpClient CreateMetadataClient() => httpClientFactory.CreateClient("ingest");
    private HttpClient CreateContentClient() => httpClientFactory.CreateClient("ingest-content");

    public async Task<IReadOnlyList<long>> GetPendingIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await CreateMetadataClient().GetFromJsonAsync<List<long>>("api/ingest/content-work", cancellationToken);
        return ids ?? [];
    }

    public async Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken)
    {
        using var response = await CreateMetadataClient().GetAsync($"api/ingest/content-work/{contentId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContentWorkItem>(cancellationToken);
    }

    public async Task CompleteAsync(long contentId, byte[] content, string? contentType, CancellationToken cancellationToken)
    {
        using var body = new ByteArrayContent(content);
        if (contentType is not null)
        {
            body.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        using var response = await CreateContentClient().PutAsync($"api/ingest/content/{contentId}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(long contentId, CancellationToken cancellationToken)
    {
        using var response = await CreateMetadataClient().PostAsync($"api/ingest/content/{contentId}/failed", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
