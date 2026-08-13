using Microsoft.Data.Sqlite;

namespace MessageService.Services;

/// <summary>Sqlite 連線字串裡的相對路徑用 ContentRootPath 為基準轉絕對路徑——不能依賴目前工作
/// 目錄，IIS in-process 與 `dotnet run` 兩種啟動方式的 CWD 行為不一致。順便確保目的目錄存在：
/// Microsoft.Data.Sqlite 不會自動建立不存在的目錄，開啟連線會直接失敗。</summary>
public static class SqliteConnectionStringResolver
{
    public static string Resolve(string connectionString, string contentRootPath)
    {
        var resolvedPath = ResolveDataSourcePath(connectionString, contentRootPath);
        if (resolvedPath is null)
        {
            return connectionString; // ":memory:"，原樣放行
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder(connectionString) { DataSource = resolvedPath };
        return builder.ConnectionString;
    }

    /// <summary>只算出解析後的資料庫檔案路徑，不建立目錄、不修改連線字串——給只需要「檔案可能
    /// 在哪裡」而不是真的要開連線的呼叫端用（例如偵測 SQLite 救場期間殘留的資料，見
    /// SqliteFallbackDataDetector）。":memory:" 回傳 null（不是檔案路徑）。</summary>
    public static string? ResolveDataSourcePath(string connectionString, string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        // ":memory:"（或未來若引入 "file::memory:?cache=shared"）不是檔案路徑，沒有對應的
        // 磁碟位置可算
        if (builder.DataSource == ":memory:")
        {
            return null;
        }

        if (Path.IsPathRooted(builder.DataSource))
        {
            return builder.DataSource;
        }

        // GetFullPath 順便正規化路徑分隔字元——appsettings.json 裡用正斜線寫相對路徑
        // （JSON 不用跳脫），組合後統一成當前作業系統慣用的分隔字元
        return Path.GetFullPath(Path.Combine(contentRootPath, builder.DataSource));
    }
}
