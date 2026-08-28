using System.Net.Http.Json;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Edge 模式用：沒有本機資料庫，打 Core 端的 heartbeat 端點代寫。跟
/// ApiContentWorkSource 共用同一個具名 HttpClient "ingest"（已經帶好 X-Ingest-Key 標頭、
/// 短 timeout，見 Program.cs 的註冊）。心跳本身不是關鍵路徑，失敗直接讓例外往外拋交給
/// HeartbeatService 記警告，不在這裡另外重試。</summary>
public class HttpHeartbeatReporter(
    IHttpClientFactory httpClientFactory, IOptions<DeploymentOptions> deploymentOptions,
    EdgeChannelState channelState) : IHeartbeatReporter
{
    public async Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken)
    {
        var request = new HeartbeatRequest(
            deploymentOptions.Value.Mode.ToString(), Environment.MachineName, report.OutboxPending, report.OutboxOldestAgeSeconds);

        using var response = await httpClientFactory.CreateClient("ingest")
            .PostAsJsonAsync("api/ingest/heartbeat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // 心跳送到了＝edge→core 方向是通的。Auto 暫停期間心跳仍每個週期照打（它不經通道閘門），
        // 等於每分鐘一次的天然探測——不通知通道狀態的話會出現雙盲窗口：Core 收到推送心跳就停止
        // 輪詢，Edge 的轉發卻還在暫停期等下一次探測（最長 ChannelProbeIntervalMinutes），
        // 訊息就這樣卡住最長一個探測週期。失敗側刻意不通知：心跳偶發失敗不該把轉發拖入暫停
        channelState.MarkPushSucceeded();
    }
}
