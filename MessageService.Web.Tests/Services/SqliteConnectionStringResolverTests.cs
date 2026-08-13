using Microsoft.Data.Sqlite;
using MessageService.Services;

namespace MessageService.Tests.Services;

public class SqliteConnectionStringResolverTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"resolver-test-{Guid.NewGuid():N}");

    [Fact]
    public void RelativePath_ResolvesAgainstContentRoot_AndCreatesDirectory()
    {
        var resolved = SqliteConnectionStringResolver.Resolve("Data Source=Db/messages.db", _contentRoot);

        var dataSource = new SqliteConnectionStringBuilder(resolved).DataSource;
        Assert.Equal(Path.Combine(_contentRoot, "Db", "messages.db"), dataSource);
        Assert.True(Directory.Exists(Path.Combine(_contentRoot, "Db")));
    }

    [Fact]
    public void AbsolutePath_PassesThroughUnchanged_ButStillCreatesDirectory()
    {
        var absoluteDb = Path.Combine(_contentRoot, "elsewhere", "messages.db");
        var resolved = SqliteConnectionStringResolver.Resolve($"Data Source={absoluteDb}", _contentRoot);

        var dataSource = new SqliteConnectionStringBuilder(resolved).DataSource;
        Assert.Equal(absoluteDb, dataSource);
        Assert.True(Directory.Exists(Path.Combine(_contentRoot, "elsewhere")));
    }

    [Fact]
    public void InMemoryDataSource_PassesThroughUntouched()
    {
        const string connectionString = "Data Source=:memory:";

        var resolved = SqliteConnectionStringResolver.Resolve(connectionString, _contentRoot);

        Assert.Equal(connectionString, resolved);
        Assert.False(Directory.Exists(_contentRoot));
    }

    [Fact]
    public void CalledTwice_DoesNotThrow_WhenDirectoryAlreadyExists()
    {
        SqliteConnectionStringResolver.Resolve("Data Source=Db/messages.db", _contentRoot);
        var resolved = SqliteConnectionStringResolver.Resolve("Data Source=Db/messages.db", _contentRoot);

        var dataSource = new SqliteConnectionStringBuilder(resolved).DataSource;
        Assert.Equal(Path.Combine(_contentRoot, "Db", "messages.db"), dataSource);
    }

    [Fact]
    public void ResolveDataSourcePath_RelativePath_DoesNotCreateDirectory()
    {
        var path = SqliteConnectionStringResolver.ResolveDataSourcePath("Data Source=Db/messages.db", _contentRoot);

        Assert.Equal(Path.Combine(_contentRoot, "Db", "messages.db"), path);
        Assert.False(Directory.Exists(Path.Combine(_contentRoot, "Db")));
    }

    [Fact]
    public void ResolveDataSourcePath_InMemory_ReturnsNull()
    {
        Assert.Null(SqliteConnectionStringResolver.ResolveDataSourcePath("Data Source=:memory:", _contentRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
