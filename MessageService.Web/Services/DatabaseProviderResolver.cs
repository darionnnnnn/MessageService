namespace MessageService.Services;

/// <summary>Database:Provider 顯式設定永遠優先；未設定時依 ConnectionStrings:SqlServer 有沒有值
/// 推導（需求2）。純函式，不做任何 I/O——SQL Server 連不連得上是另一層判斷，見
/// DatabaseStartupProbe，這裡只決定「沒填 Provider 時該預期用哪個」。</summary>
public static class DatabaseProviderResolver
{
    public static (string Provider, bool WasInferred) Resolve(string? configuredProvider, bool hasSqlServerConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            // 大小寫在這裡收斂成標準寫法——下游（DbContext 註冊、migration、validator）全部用
            // == "SqlServer" 精確比對，顯式設 "sqlserver" 若不正規化會靜默落入 Sqlite 分支，
            // 而且兩條驗證規則都不會觸發，完全沒有提示。無法辨認的值維持原樣（下游行為跟
            // 既往一致：非 SqlServer 一律走 Sqlite 分支）
            if (string.Equals(configuredProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return ("SqlServer", false);
            }
            if (string.Equals(configuredProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                return ("Sqlite", false);
            }
            return (configuredProvider, false);
        }

        return (hasSqlServerConnectionString ? "SqlServer" : "Sqlite", true);
    }
}
