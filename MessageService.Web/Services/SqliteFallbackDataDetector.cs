using Microsoft.Data.Sqlite;

namespace MessageService.Services;

/// <summary>SQL Server 探測成功、正常啟動時，偵測站台目錄下是否殘留 SQLite 救場期間累積的
/// 資料——只偵測、只記警告，不做任何自動合併（見 Program.cs 的取捨：合併涉及主鍵、blob、
/// 群組狀態對帳，複雜度跟這個邊角案例的價值不成比例）。</summary>
public static class SqliteFallbackDataDetector
{
    public static bool HasResidualMessages(string sqliteFilePath)
    {
        if (!File.Exists(sqliteFilePath))
        {
            return false;
        }

        using var connection = new SqliteConnection($"Data Source={sqliteFilePath};Mode=ReadOnly");
        connection.Open();

        using var checkTable = connection.CreateCommand();
        checkTable.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'GroupMessages'";
        if (checkTable.ExecuteScalar() is null)
        {
            return false;
        }

        using var checkRows = connection.CreateCommand();
        checkRows.CommandText = "SELECT EXISTS(SELECT 1 FROM GroupMessages)";
        return Convert.ToInt64(checkRows.ExecuteScalar()) == 1;
    }
}
