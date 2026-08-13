using MessageService.Options;

namespace MessageService.Services;

/// <summary>依部署模式（與各自的 override 設定）推導出這台主機實際要做的六件事。單點化這份推導
/// 邏輯，是為了不重蹈「同一份判斷散在 Program.cs 好幾處、改一處漏改另一處」的覆轍——
/// 之前在別的專案（LatestActivity）踩過這種漏改讀取端的回歸，這裡從一開始就只寫一次。</summary>
public record DeploymentCapabilities(
    bool ReceivesWebhook,
    bool HasDatabaseAccess,
    bool IngestApiEnabled,
    bool ViewerEnabled,
    bool OutboundHere,
    bool RunsRetention)
{
    public static DeploymentCapabilities Derive(DeploymentMode mode, LineOptions line, ViewerOptions viewer, IngestOptions ingest)
    {
        var hasDatabaseAccess = mode is DeploymentMode.AllInOne or DeploymentMode.Core or DeploymentMode.Viewer;
        var receivesWebhook = mode is DeploymentMode.AllInOne or DeploymentMode.Edge;

        // ingest API 只在 AllInOne／Core 存在（即使 Viewer 模式也直連資料庫，仍不該多開這個
        // 寫入端點）——跟 hasDatabaseAccess 分開判斷，避免日後 hasDatabaseAccess 的定義擴張時
        // 這裡被連帶影響
        var ingestApiEnabled = mode is DeploymentMode.AllInOne or DeploymentMode.Core
            && !string.IsNullOrWhiteSpace(ingest.ApiKey);

        // 夾住 hasDatabaseAccess：檢視端整組服務都要 MessageDbContext，Edge 顯式設
        // Viewer:Enabled=true（多半是從別台主機複製設定忘記清）若照單全收，服務註冊矩陣會
        // 註冊出解析不了的相依、炸出難懂的 DI 錯誤——這裡先夾住讓註冊矩陣保持一致，
        // 人話版的啟動錯誤由 DeploymentValidator 負責
        var viewerEnabled = (viewer.Enabled ?? hasDatabaseAccess) && hasDatabaseAccess;
        var outboundHere = line.OutboundHere ?? (mode is DeploymentMode.AllInOne or DeploymentMode.Edge);

        // 保留期清除只在 AllInOne／Core 跑——三台拓撲下恰好一台（Core）負責，Viewer 模式
        // 即使也直連資料庫也不跑，避免多實例同時清除同一張表
        var runsRetention = mode is DeploymentMode.AllInOne or DeploymentMode.Core;

        return new DeploymentCapabilities(receivesWebhook, hasDatabaseAccess, ingestApiEnabled, viewerEnabled, outboundHere, runsRetention);
    }
}
