using MessageService.Options;

namespace MessageService.Services;

/// <summary>設定沒對齊部署模式時，讓程式啟動就失敗——帶著錯誤設定悄悄跑起來（例如缺了
/// ChannelSecret 卻一直收 webhook、永遠 401）遠比啟動失敗更難發現，尤其是網段分離的部署，
/// 出問題時往往沒辦法立刻連上去看 log。</summary>
public static class DeploymentValidator
{
    public static void Validate(DeploymentOptions deployment, LineOptions line, IngestOptions ingest, ILogger logger)
    {
        if (deployment.Mode == DeploymentMode.Line &&
            (string.IsNullOrWhiteSpace(ingest.BaseUrl) || string.IsNullOrWhiteSpace(ingest.ApiKey)))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Line 需要設定 Ingest:BaseUrl（Db 模式主機的 ingest API 位址）與 " +
                "Ingest:ApiKey（雙邊共用密鑰，須與 Db 端一致），否則 outbox 排出的事件無處可送。");
        }

        if (deployment.Mode == DeploymentMode.Db && string.IsNullOrWhiteSpace(ingest.ApiKey))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Db 需要設定 Ingest:ApiKey 以驗證 /api/ingest/* 進來的請求。");
        }

        if (deployment.Mode is DeploymentMode.Full or DeploymentMode.Line && string.IsNullOrWhiteSpace(line.ChannelSecret))
        {
            throw new InvalidOperationException(
                "這個模式會收 LINE webhook（Deployment:Mode=Full 或 Line），需要設定 Line:ChannelSecret 才能驗證簽章。");
        }

        // Stage 3：Line:OutboundHere 現在真的決定 ContentDownloadService／ProfileRefreshService
        // 會不會在這台主機跑（見 Program.cs 的註冊矩陣）——OutboundHere=true 卻沒有
        // ChannelAccessToken，這兩個背景服務會直接對 LINE profile／content API 打 401，
        // 而且不是啟動就爆炸、是跑起來後才悄悄一直失敗，所以要擋在啟動關卡
        if (line.OutboundHere && string.IsNullOrWhiteSpace(line.ChannelAccessToken))
        {
            throw new InvalidOperationException(
                "Line:OutboundHere=true 時需要設定 Line:ChannelAccessToken，" +
                "否則媒體下載與頭貼快取會在背景服務啟動後持續打 401。");
        }

        // Full 模式關掉媒體下載／頭貼快取是可疑的設定組合（單機部署通常沒有理由要這樣做），
        // 但不是錯誤——只記警告，不擋啟動
        if (deployment.Mode == DeploymentMode.Full && !line.OutboundHere)
        {
            logger.LogWarning(
                "Deployment:Mode=Full 但 Line:OutboundHere=false：媒體下載與頭貼快取不會執行，" +
                "所有訊息內容會停在 Pending。如果這不是刻意的，請檢查設定。");
        }
    }
}
