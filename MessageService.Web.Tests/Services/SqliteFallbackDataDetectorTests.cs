using Microsoft.Data.Sqlite;
using MessageService.Services;

namespace MessageService.Tests.Services;

public class SqliteFallbackDataDetectorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fallback-detector-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void FileDoesNotExist_ReturnsFalse()
    {
        Assert.False(SqliteFallbackDataDetector.HasResidualMessages(_dbPath));
    }

    [Fact]
    public void FileExists_ButNoGroupMessagesTable_ReturnsFalse()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE SomeOtherTable (Id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.False(SqliteFallbackDataDetector.HasResidualMessages(_dbPath));
    }

    [Fact]
    public void GroupMessagesTable_Empty_ReturnsFalse()
    {
        CreateGroupMessagesTable(rowCount: 0);

        Assert.False(SqliteFallbackDataDetector.HasResidualMessages(_dbPath));
    }

    [Fact]
    public void GroupMessagesTable_HasRows_ReturnsTrue()
    {
        CreateGroupMessagesTable(rowCount: 3);

        Assert.True(SqliteFallbackDataDetector.HasResidualMessages(_dbPath));
    }

    private void CreateGroupMessagesTable(int rowCount)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE GroupMessages (Id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }

        for (var i = 0; i < rowCount; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO GroupMessages (Id) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", i + 1);
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
