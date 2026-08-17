using MessageService.Data;
using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageService.Tests.Services;

public class MessageServiceDatabaseMigrationExtensionsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"migration-log-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void MigrateMessageServiceDatabase_WithPendingMigrations_LogsMigrationNamesAndElapsed()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "Sqlite";
        builder.Configuration["ConnectionStrings:Sqlite"] = ConnectionString;
        builder.Configuration["Database:AutoMigrate"] = "true";

        var logger = new CapturingLogger<Program>();
        builder.Services.AddSingleton<ILogger<Program>>(logger);

        var ingestOptions = new IngestOptions();
        var capabilities = DeploymentCapabilities.Derive(
            DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions);
        var registration = builder.AddMessageServiceCore(capabilities, DeploymentMode.AllInOne, ingestOptions);

        using var app = builder.Build();
        app.MigrateMessageServiceDatabase(capabilities, registration);

        // 驗證有待套用 migration 時記錄了數量與名稱清單
        var startLog = Assert.Single(logger.Logs, l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("待套用") &&
            l.Message.Contains("InitialCreate"));

        Assert.Contains("Sqlite", startLog.Message);

        // 驗證完成後記錄了耗時
        var finishLog = Assert.Single(logger.Logs, l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("套用完成") &&
            l.Message.Contains("ms"));

        Assert.Contains("Sqlite", finishLog.Message);
    }

    [Fact]
    public void MigrateMessageServiceDatabase_WhenUpToDate_LogsUpToDateWithoutMigrationList()
    {
        // 先建立並跑完 migration 讓資料庫處於最新狀態
        var builder1 = WebApplication.CreateBuilder();
        builder1.Configuration["Database:Provider"] = "Sqlite";
        builder1.Configuration["ConnectionStrings:Sqlite"] = ConnectionString;
        builder1.Configuration["Database:AutoMigrate"] = "true";
        var ingestOptions1 = new IngestOptions();
        var capabilities1 = DeploymentCapabilities.Derive(
            DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions1);
        var registration1 = builder1.AddMessageServiceCore(capabilities1, DeploymentMode.AllInOne, ingestOptions1);

        using (var app1 = builder1.Build())
        {
            app1.MigrateMessageServiceDatabase(capabilities1, registration1);
        }

        // 第二次啟動，此時資料庫已是最新
        var builder2 = WebApplication.CreateBuilder();
        builder2.Configuration["Database:Provider"] = "Sqlite";
        builder2.Configuration["ConnectionStrings:Sqlite"] = ConnectionString;
        builder2.Configuration["Database:AutoMigrate"] = "true";

        var logger = new CapturingLogger<Program>();
        builder2.Services.AddSingleton<ILogger<Program>>(logger);

        var ingestOptions2 = new IngestOptions();
        var capabilities2 = DeploymentCapabilities.Derive(
            DeploymentMode.AllInOne, new LineOptions(), new ViewerOptions(), ingestOptions2);
        var registration2 = builder2.AddMessageServiceCore(capabilities2, DeploymentMode.AllInOne, ingestOptions2);

        using var app2 = builder2.Build();
        app2.MigrateMessageServiceDatabase(capabilities2, registration2);

        // 驗證記錄了「已是最新」訊息
        var upToDateLog = Assert.Single(logger.Logs, l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("已是最新") &&
            l.Message.Contains("不需套用"));

        Assert.Contains("Sqlite", upToDateLog.Message);

        // 驗證未記錄待套用清單或套用完成耗時
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("待套用"));
        Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("套用完成"));
    }
}
