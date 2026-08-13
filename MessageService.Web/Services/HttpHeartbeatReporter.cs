using System.Net.Http.Json;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Edge 模式用：沒有本機資料庫，打 Core 端的 heartbeat 端點代寫。跟
/// ApiContentWorkSource 共用同一個具名 HttpClient "ingest"（已經帶好 X-Ingest-Key 標頭、
/// 短 timeout，見 Program.cs 的註冊）。心跳本身不是關鍵路徑，失敗直接讓例外往外拋交給
/// HeartbeatService 記警告，不在這裡另外重試。</summary>
public class HttpHeartbeatReporter(IHttpClientFactory httpClientFactory, IOptions<DeploymentOptions> deploymentOptions)
    : IHeartbeatReporter
{
    public async Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken)
    {
        var request = new HeartbeatRequest(
            deploymentOptions.Value.Mode.ToString(), Environment.MachineName, report.OutboxPending, report.OutboxOldestAgeSeconds);

        using var response = await httpClientFactory.CreateClient("ingest")
            .PostAsJsonAsync("api/ingest/heartbeat", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
