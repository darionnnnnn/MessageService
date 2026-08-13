namespace MessageService.Services;

/// <summary>啟動時「該用哪個資料庫 provider」這整段決策的結果，集中成一個物件——推導、
/// SQLite 救場、DeploymentValidator 的檢查、設定頁曝露給前端的狀態都讀同一份資料，不用各自
/// 從一堆散落的 bool／string 參數重新兜一次（跟 DeploymentCapabilities 單點化的理由一樣）。
/// 單例：只在啟動時決定一次，行程存續期間不變，見 Program.cs 對「不做執行中動態切換」的說明。</summary>
public record DatabaseStartupDecision(
    string? ConfiguredProvider,
    string EffectiveProvider,
    bool ProviderWasInferred,
    bool HasSqlServerConnectionString,
    bool SqliteFallbackConfigured,
    bool SqliteFallbackEnabled,
    bool SqliteFallbackTriggered,
    string? SqliteFallbackReason)
{
    /// <summary>DeploymentValidator 的單元測試不關心 DB 推導細節時的預設值——等同「未設定
    /// Provider、沒有 SqlServer 連線字串、沒有觸發救場」，跟批次 B 之前的行為一致。</summary>
    public static DatabaseStartupDecision Default { get; } = new(
        ConfiguredProvider: null,
        EffectiveProvider: "Sqlite",
        ProviderWasInferred: true,
        HasSqlServerConnectionString: false,
        SqliteFallbackConfigured: false,
        SqliteFallbackEnabled: true,
        SqliteFallbackTriggered: false,
        SqliteFallbackReason: null);
}
