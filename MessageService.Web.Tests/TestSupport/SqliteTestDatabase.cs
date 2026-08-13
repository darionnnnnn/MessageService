using Microsoft.Data.Sqlite;

namespace MessageService.Tests.TestSupport;

public static class SqliteTestDatabase
{
    public static SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
    }
}
