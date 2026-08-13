using MessageService.Data;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Services;

public class DbHeartbeatStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public DbHeartbeatStoreTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private DbHeartbeatStore CreateStore(MessageDbContext dbContext) => new(dbContext);

    [Fact]
    public async Task UpsertAsync_NewRoleAndMachine_InsertsRow()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var store = CreateStore(dbContext);

        var before = DateTimeOffset.UtcNow;
        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(5, 12.5), "abcd1234", CancellationToken.None);

        var row = await dbContext.HostHeartbeats.AsNoTracking().SingleAsync(h => h.Role == "Core" && h.MachineName == "host-1");
        Assert.True(row.LastSeenAt >= before);
        Assert.Equal(5, row.OutboxPending);
        Assert.Equal(12.5, row.OutboxOldestAgeSeconds);
        Assert.Equal("abcd1234", row.EncryptionKeyFingerprint);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_UpdatesInPlace_DoesNotDuplicate()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var store = CreateStore(dbContext);

        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(5, 12.5), "abcd1234", CancellationToken.None);
        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(0, null), "abcd1234", CancellationToken.None);

        var rows = await dbContext.HostHeartbeats.AsNoTracking().Where(h => h.Role == "Core" && h.MachineName == "host-1").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(0, row.OutboxPending);
        Assert.Null(row.OutboxOldestAgeSeconds);
    }

    [Fact]
    public async Task UpsertAsync_DifferentRolesOrMachines_CoexistAsSeparateRows()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var store = CreateStore(dbContext);

        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(null, null), null, CancellationToken.None);
        await store.UpsertAsync("Edge", "host-1", new HeartbeatReport(2, 3), null, CancellationToken.None);
        await store.UpsertAsync("Core", "host-2", new HeartbeatReport(null, null), null, CancellationToken.None);

        var count = await dbContext.HostHeartbeats.CountAsync();
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task UpsertAsync_NullFingerprint_StoresNull()
    {
        // Edge 代寫的心跳一律不帶指紋——見 IngestController.ReportHeartbeat 的說明
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var store = CreateStore(dbContext);

        await store.UpsertAsync("Edge", "edge-1", new HeartbeatReport(1, 2), null, CancellationToken.None);

        var row = await dbContext.HostHeartbeats.AsNoTracking().SingleAsync(h => h.Role == "Edge" && h.MachineName == "edge-1");
        Assert.Null(row.EncryptionKeyFingerprint);
    }
}
