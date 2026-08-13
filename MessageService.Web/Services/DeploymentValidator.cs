using MessageService.Options;

namespace MessageService.Services;

/// <summary>設定沒對齊部署模式時，讓程式啟動就失敗——帶著錯誤設定悄悄跑起來（例如缺了
/// ChannelSecret 卻一直收 webhook、永遠 401）遠比啟動失敗更難發現，尤其是網段分離的部署，
/// 出問題時往往沒辦法立刻連上去看 log。</summary>
public static class DeploymentValidator
{
    public static void Validate(
        DeploymentOptions deployment, LineOptions line, ViewerOptions viewer, IngestOptions ingest, ILogger logger,
        string? databaseProvider = null, bool hasSqlServerConnectionString = false)
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

        // Edge 沒有資料庫連線，檢視端整組服務都開不起來——顯式設 true 多半是從別台主機複製
        // appsettings 忘記清（跟下面 Viewer 模式殘留設定的警告同一種失誤），但這個設錯不是
        // 「多餘設定」而是「期待的功能不會存在」，寧可啟動失敗講清楚，不要讓人以為檢視端有開
        if (!capabilities.HasDatabaseAccess && viewer.Enabled == true)
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Edge 沒有資料庫連線，無法啟用檢視端（Viewer:Enabled=true）。" +
                "請移除這個設定，或改用 AllInOne／Core／Viewer 模式。");
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

        // Core／Viewer 顯式把 OutboundHere 開成 true 表示「由這台打 LINE 內容／profile API」，
        // 此時 Edge 端必須顯式設 false，否則兩台都會下載同一批媒體（LINE 內容 API 不冪等計費、
        // 也浪費頻寬）。這種跨主機的組合錯誤單機驗證不出來，只能提醒
        if (mode is DeploymentMode.Core or DeploymentMode.Viewer && line.OutboundHere == true)
        {
            logger.LogWarning(
                "Deployment:Mode={Mode} 顯式設定了 Line:OutboundHere=true：請確認 Edge 端主機已顯式設 " +
                "Line:OutboundHere=false，否則兩台會重複下載同一批媒體內容。", mode);
        }

        // AllInOne 模式關掉媒體下載／頭貼快取是可疑的設定組合（單機部署通常沒有理由要這樣做），
        // 但不是錯誤——只記警告，不擋啟動
        if (mode is DeploymentMode.AllInOne && !capabilities.OutboundHere)
        {
            logger.LogWarning(
                "Deployment:Mode=AllInOne 但 Line:OutboundHere 判定為 false：媒體下載與頭貼快取不會執行，" +
                "所有訊息內容會停在 Pending。如果這不是刻意的，請檢查設定。");
        }

        // 檢視端啟用時預設會一併開（見 DeploymentCapabilities.ViewerEnabled）——空白名單雖然是
        // 「全拒」而非啟動失敗，但這種組合通常代表部署時漏設，值得提醒。不限 Core：AllInOne
        // 是最常見的拓撲，同樣會「檢視端啟用了卻全拒」而不自知
        if (capabilities.ViewerEnabled && viewer.AllowedClientIps.Length == 0)
        {
            logger.LogWarning(
                "Deployment:Mode={Mode} 且檢視端已啟用，但 Viewer:AllowedClientIps 是空的——檢視端會拒絕所有請求，" +
                "直到設定允許的來源網段為止。", mode);
        }

        // Provider 鍵決定啟動時實際連的是哪個資料庫（見 Program.cs 的 databaseProvider 判斷）——
        // 顯式維持 Sqlite 預設、不做「有連線字串就自動切換」的隱式推導（殘留設定不該悄悄換
        // 資料庫），但這代表打錯 Provider 鍵或忘記改的情況只能靠這條警告攔：已經設定了
        // SqlServer 連線字串，Provider 卻還是 Sqlite，多半是想切換但忘了改這個鍵
        if (databaseProvider == "Sqlite" && hasSqlServerConnectionString)
        {
            logger.LogWarning(
                "Database:Provider 是 Sqlite，但 ConnectionStrings:SqlServer 有設定值——這個連線字串不會被使用。" +
                "如果是想改用 SQL Server，請把 Database:Provider 改成 \"SqlServer\"；如果只是複製設定殘留，" +
                "可以移除這個連線字串。");
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
