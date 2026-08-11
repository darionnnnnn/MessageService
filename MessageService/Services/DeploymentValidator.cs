using MessageService.Options;

namespace MessageService.Services;

/// <summary>設定沒對齊部署模式時，讓程式啟動就失敗——帶著錯誤設定悄悄跑起來（例如缺了
/// ChannelSecret 卻一直收 webhook、永遠 401）遠比啟動失敗更難發現，尤其是網段分離的部署，
/// 出問題時往往沒辦法立刻連上去看 log。</summary>
public static class DeploymentValidator
{
    public static void Validate(DeploymentOptions deployment, LineOptions line, IngestOptions ingest, ILogger logger)
    {
        if (deployment.Mode == DeploymentMode.Line)
        {
            // Stage 2（ingest API 客戶端 HttpIngestSink）尚未實作——先讓啟動失敗，避免收了
            // webhook、寫進本機 outbox，卻永遠沒有東西把它排空。目前只能先用 Full 模式。
            throw new InvalidOperationException(
                "Deployment:Mode=Line 需要 ingest API 客戶端（規劃中的 Stage 2），目前尚未實作，" +
                "且屆時需要設定 Ingest:BaseUrl 與 Ingest:ApiKey。請先使用 Deployment:Mode=Full。");
        }

        if (deployment.Mode == DeploymentMode.Db && string.IsNullOrWhiteSpace(ingest.ApiKey))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Db 需要設定 Ingest:ApiKey 以驗證進來的請求（規劃中的 Stage 2 端點會用它）。");
        }

        if (deployment.Mode is DeploymentMode.Full or DeploymentMode.Line && string.IsNullOrWhiteSpace(line.ChannelSecret))
        {
            throw new InvalidOperationException(
                "這個模式會收 LINE webhook（Deployment:Mode=Full 或 Line），需要設定 Line:ChannelSecret 才能驗證簽章。");
        }

        // Line:OutboundHere 目前（Stage 1）還沒有任何註冊邏輯依據它做決定——它要等 Stage 3
        // 的 IContentWorkSource 落地才會真正生效，所以這裡刻意不要求 ChannelAccessToken，
        // 避免對還沒用到這個設定的部署造成不必要的啟動失敗。見 docs/DEPLOYMENT-MODES.md。
    }
}
