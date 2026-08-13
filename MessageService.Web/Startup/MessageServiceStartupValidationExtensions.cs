using MessageService.Data.Crypto;
using MessageService.Options;
using MessageService.Services;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Startup;

/// <summary>Program.cs 里純機械式拆出來的結果——不改任何邏輯／順序／條件，只是把「啟動時的
/// 設定健檢」這段（部署模式驗證、DB provider 推導與救場記 log、殘留救場資料偵測、FieldCipher
/// 強制解析）搬進這個檔案。</summary>
public static class MessageServiceStartupValidationExtensions
{
    public static void ValidateMessageServiceStartup(
        this WebApplication app,
        DeploymentOptions deploymentOptions,
        DeploymentMode deploymentMode,
        bool usedLegacyModeName,
        string? rawModeValue,
        MessageServiceCoreRegistration registration)
    {
        using var validationScope = app.Services.CreateScope();
        var validationLogger = validationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        if (usedLegacyModeName)
        {
            validationLogger.LogWarning(
                "Deployment:Mode 使用了舊名稱 \"{RawValue}\"，會自動對應到新名稱" +
                "（Full→AllInOne／Line→Edge／Db→Core），但建議更新設定檔改用新名稱。",
                rawModeValue);
        }

        // 需求2：純粹的推導結果記一行 Info——救場觸發（Error 等級）與其他 DB 相關規則檢查都在
        // DeploymentValidator 裡，這裡只記錄「沒設 Provider 時自動選了哪個」這個單純事實
        var decision = registration.DatabaseStartupDecision;
        if (decision.ProviderWasInferred)
        {
            validationLogger.LogInformation(
                "Database:Provider 未設定，依 ConnectionStrings:SqlServer 是否有值推導為 {Provider}。",
                decision.ProviderBeforeFallback);
        }

        var lineOptions = validationScope.ServiceProvider.GetRequiredService<IOptions<LineOptions>>().Value;
        var viewerOptions = validationScope.ServiceProvider.GetRequiredService<IOptions<ViewerOptions>>().Value;
        var ingestOptions = validationScope.ServiceProvider.GetRequiredService<IOptions<IngestOptions>>().Value;
        DeploymentValidator.Validate(
            deploymentOptions, lineOptions, viewerOptions, ingestOptions, validationLogger,
            decision);

        // 救場沒有觸發、確實在用 SQL Server：偵測站台目錄下是否殘留救場期間累積的 SQLite 資料——
        // 只偵測、只警告，不自動合併（見 AddMessageServiceCore 救場區塊的說明）。用跟主資料庫相同
        // 的預設路徑解析規則找「如果有救場資料，會在哪裡」，不需要真的建立這個目錄
        // （ResolveDataSourcePath 不像 Resolve 那樣有建立目錄的副作用）
        if (deploymentMode is DeploymentMode.AllInOne && decision.EffectiveProvider == "SqlServer")
        {
            var potentialFallbackPath = SqliteConnectionStringResolver.ResolveDataSourcePath(
                app.Configuration.GetConnectionString("Sqlite") ?? "Data Source=Db/messages.db",
                app.Environment.ContentRootPath);
            if (potentialFallbackPath is not null)
            {
                // 這只是「順手警告」性質的偵測——殘留檔案損毀、非 SQLite 格式或被別的行程鎖住
                // 都不該讓 SQL Server 一切正常的部署啟動失敗，吞掉例外改記警告
                try
                {
                    if (SqliteFallbackDataDetector.HasResidualMessages(potentialFallbackPath))
                    {
                        validationLogger.LogWarning(
                            "偵測到 {Path} 有資料——這通常代表先前 SQLite 救場期間累積過訊息，但尚未存在於目前" +
                            "使用的 SQL Server 裡。不會自動合併，請視需要人工處理，見 docs/DEPLOYMENT-GUIDE.md。",
                            potentialFallbackPath);
                    }
                }
                catch (Exception ex)
                {
                    validationLogger.LogWarning(ex,
                        "檢查 {Path} 是否殘留 SQLite 救場資料時失敗（檔案損毀或被鎖住？）——不影響啟動，" +
                        "但無法確認是否有救場期間的資料尚未搬到 SQL Server。", potentialFallbackPath);
                }
            }
        }

        // FieldCipher 是單例，第一次被解析時才會驗證 Encryption:Key（Enabled=true 但金鑰缺漏／
        // 格式錯誤會在建構子裡丟例外）——這裡強制在啟動當下就解析一次，壞設定要讓服務直接
        // 啟動失敗，不要等到第一則訊息進來才在背景任務裡炸開
        validationScope.ServiceProvider.GetRequiredService<FieldCipher>();
    }
}
