using MessageService.Options;

namespace MessageService.Services;

/// <summary>設定沒對齊部署模式時，讓程式啟動就失敗——帶著錯誤設定悄悄跑起來（例如缺了
/// ChannelSecret 卻一直收 webhook、永遠 401）遠比啟動失敗更難發現，尤其是網段分離的部署，
/// 出問題時往往沒辦法立刻連上去看 log。</summary>
public static class DeploymentValidator
{
    public static void Validate(DeploymentOptions deployment, LineOptions line, ViewerOptions viewer, IngestOptions ingest, ILogger logger)
    {
        var mode = deployment.Mode;
        var capabilities = DeploymentCapabilities.Derive(mode, line, viewer, ingest);

        if (mode is DeploymentMode.Edge &&
            (string.IsNullOrWhiteSpace(ingest.BaseUrl) || string.IsNullOrWhiteSpace(ingest.ApiKey)))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Edge 需要設定 Ingest:BaseUrl（Core 模式主機的 ingest API 位址）與 " +
                "Ingest:ApiKey（雙邊共用密鑰，須與 Core 端一致），否則 outbox 排出的事件無處可送。");
        }

        if (mode is DeploymentMode.Core && string.IsNullOrWhiteSpace(ingest.ApiKey))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Core 需要設定 Ingest:ApiKey 以驗證 /api/ingest/* 進來的請求。");
        }

        if (capabilities.ReceivesWebhook && string.IsNullOrWhiteSpace(line.ChannelSecret))
        {
            throw new InvalidOperationException(
                "這個模式會收 LINE webhook（Deployment:Mode=AllInOne 或 Edge），需要設定 Line:ChannelSecret 才能驗證簽章。");
        }

        // Line:OutboundHere 現在真的決定 ContentDownloadService／ProfileRefreshService 會不會在
        // 這台主機跑（見 Program.cs 的註冊矩陣，經 DeploymentCapabilities 推導）——OutboundHere
        // 判定為 true 卻沒有 ChannelAccessToken，這兩個背景服務會直接對 LINE profile／content API
        // 打 401，而且不是啟動就爆炸、是跑起來後才悄悄一直失敗，所以要擋在啟動關卡
        if (capabilities.OutboundHere && string.IsNullOrWhiteSpace(line.ChannelAccessToken))
        {
            throw new InvalidOperationException(
                "這台主機會對外呼叫 LINE API（Line:OutboundHere 判定為 true），需要設定 " +
                "Line:ChannelAccessToken，否則媒體下載與頭貼快取會在背景服務啟動後持續打 401。");
        }

        // AllInOne 模式關掉媒體下載／頭貼快取是可疑的設定組合（單機部署通常沒有理由要這樣做），
        // 但不是錯誤——只記警告，不擋啟動
        if (mode is DeploymentMode.AllInOne && !capabilities.OutboundHere)
        {
            logger.LogWarning(
                "Deployment:Mode=AllInOne 但 Line:OutboundHere 判定為 false：媒體下載與頭貼快取不會執行，" +
                "所有訊息內容會停在 Pending。如果這不是刻意的，請檢查設定。");
        }

        // Core 模式預設會一併開檢視端（見 DeploymentCapabilities.ViewerEnabled）——空白名單雖然
        // 是「全拒」而非啟動失敗，但這種組合通常代表部署時漏設，值得提醒
        if (mode is DeploymentMode.Core && capabilities.ViewerEnabled && viewer.AllowedClientIps.Length == 0)
        {
            logger.LogWarning(
                "Deployment:Mode=Core 且檢視端已啟用，但 Viewer:AllowedClientIps 是空的——檢視端會拒絕所有請求，" +
                "直到設定允許的來源網段為止。");
        }

        // Viewer 模式不會用到 Line／Ingest 設定——多半是從別台主機複製 appsettings 忘記清掉，
        // 不是錯誤但值得提醒，免得誤以為這些設定在 Viewer 模式下也有作用
        if (mode is DeploymentMode.Viewer &&
            (!string.IsNullOrWhiteSpace(line.ChannelSecret) || !string.IsNullOrWhiteSpace(ingest.BaseUrl) || !string.IsNullOrWhiteSpace(ingest.ApiKey)))
        {
            logger.LogWarning(
                "Deployment:Mode=Viewer 不會用到 Line／Ingest 設定，但偵測到有值——" +
                "可能是從其他主機複製 appsettings 時忘記清掉，請確認是否為刻意保留。");
        }
    }
}
