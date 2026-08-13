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
            return (configuredProvider, false);
        }

        return (hasSqlServerConnectionString ? "SqlServer" : "Sqlite", true);
    }
}
