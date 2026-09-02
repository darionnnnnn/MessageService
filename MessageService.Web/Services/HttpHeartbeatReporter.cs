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

        try
        {
            using var response = await httpClientFactory.CreateClient("ingest")
                .PostAsJsonAsync("api/ingest/heartbeat", request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 真的在停機才放行；HttpClient 逾時丟的 TaskCanceledException 也是 OperationCanceledException，
            // 用例外型別過濾會把「Core 沒回應」誤當成「呼叫端取消」而不計入（見 ContentDownloadService
            // 的同一教訓）。判斷依據看 token 本身
            throw;
        }
        catch
        {
            // 心跳是這個方向唯一「不受通道閘門節流、固定每個週期都會送」的流量，因此也是
            // 判斷 edge→core 通不通最靈敏的訊號。**沒有訊息流量時 outbox 根本不會嘗試推送**，
            // 只靠 OutboxForwarderService 通知的話，安靜的站台永遠不會切換到拉取資源，
            // 名稱／頭貼與媒體會一直打向不通的 Core（實測到的症狀）。
            //
            // 偶發失敗不會誤觸暫停：EdgeChannelState 有 PullActivationSeconds（預設 180 秒）
            // 的寬限期，心跳 60 秒一次代表要連續失敗三次以上才會真的暫停。
            // 任何失敗都計入（含 4xx，例如 ingest 金鑰錯）——語意是「推送通道未確認可用」。
            channelState.MarkPushFailed();
            throw;
        }

        // 心跳送到了＝edge→core 方向是通的。Auto 暫停期間心跳仍每個週期照打（它不經通道閘門），
        // 等於每分鐘一次的天然探測——不通知通道狀態的話會出現雙盲窗口：Core 收到推送心跳就停止
        // 輪詢，Edge 的轉發卻還在暫停期等下一次探測（最長 ChannelProbeIntervalMinutes），
        // 訊息就這樣卡住最長一個探測週期。
        channelState.MarkPushSucceeded();
    }
}
