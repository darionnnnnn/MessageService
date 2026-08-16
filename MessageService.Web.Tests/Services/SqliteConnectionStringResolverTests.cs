using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MessageService.Data;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Web.Startup;

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

    [Fact]
    public void SqliteMessageDbContext_FromDI_AppliesConfiguredBusyTimeout()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:Sqlite"] = "Data Source=:memory:";
        builder.Configuration["Database:SqliteBusyTimeoutMs"] = "12345";
        var ingestOptions = new IngestOptions { BaseUrl = "https://example.com" };
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions);

        builder.AddMessageServiceCore(capabilities, DeploymentMode.AllInOne, ingestOptions);
        using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        dbContext.Database.OpenConnection();

        var connection = dbContext.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var busyTimeout = Convert.ToInt64(command.ExecuteScalar());

        Assert.Equal(12345, busyTimeout);
    }

    [Fact]
    public async Task SqliteMessageDbContext_FromDI_AsyncConnectionOpened_AppliesConfiguredBusyTimeout()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:Sqlite"] = "Data Source=:memory:";
        builder.Configuration["Database:SqliteBusyTimeoutMs"] = "12345";
        var ingestOptions = new IngestOptions { BaseUrl = "https://example.com" };
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions);

        builder.AddMessageServiceCore(capabilities, DeploymentMode.AllInOne, ingestOptions);
        using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        await dbContext.Database.OpenConnectionAsync();

        var connection = dbContext.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var busyTimeout = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(12345, busyTimeout);
    }

    [Fact]
    public void OutboxDbContext_FromDI_AppliesConfiguredBusyTimeout()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:Outbox"] = "Data Source=:memory:";
        builder.Configuration["Database:SqliteBusyTimeoutMs"] = "12345";
        var ingestOptions = new IngestOptions { BaseUrl = "https://example.com" };
        var capabilities = DeploymentCapabilities.Derive(DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions);

        builder.AddMessageServiceCore(capabilities, DeploymentMode.AllInOne, ingestOptions);
        using var app = builder.Build();

        using var scope = app.Services.CreateScope();
        var outboxDbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        outboxDbContext.Database.OpenConnection();

        var connection = outboxDbContext.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var busyTimeout = Convert.ToInt64(command.ExecuteScalar());

        Assert.Equal(12345, busyTimeout);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
