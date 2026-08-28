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
        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(5, 12.5), "abcd1234", HeartbeatChannel.Push, CancellationToken.None);

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

        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(5, 12.5), "abcd1234", HeartbeatChannel.Push, CancellationToken.None);
        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(0, null), "abcd1234", HeartbeatChannel.Push, CancellationToken.None);

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

        await store.UpsertAsync("Core", "host-1", new HeartbeatReport(null, null), null, HeartbeatChannel.Push, CancellationToken.None);
        await store.UpsertAsync("Edge", "host-1", new HeartbeatReport(2, 3), null, HeartbeatChannel.Push, CancellationToken.None);
        await store.UpsertAsync("Core", "host-2", new HeartbeatReport(null, null), null, HeartbeatChannel.Push, CancellationToken.None);

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

        await store.UpsertAsync("Edge", "edge-1", new HeartbeatReport(1, 2), null, HeartbeatChannel.Push, CancellationToken.None);

        var row = await dbContext.HostHeartbeats.AsNoTracking().SingleAsync(h => h.Role == "Edge" && h.MachineName == "edge-1");
        Assert.Null(row.EncryptionKeyFingerprint);
    }

    [Fact]
    public async Task UpsertAsync_ConcurrentInsert_RetriesAndUpdatesWithoutThrowing()
    {
        var interceptor = new SaveFailureInterceptor();
        var options = new DbContextOptionsBuilder<MessageDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        using var testDbContext = new MessageDbContext(options);
        var store = CreateStore(testDbContext);

        // 模擬在第一次 SaveChanges 前，另一個行程搶先寫入了該 (Role, MachineName)
        interceptor.BeforeSaveOnce = async () =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO HostHeartbeats (Role, MachineName, LastSeenAt, OutboxPending, OutboxOldestAgeSeconds, EncryptionKeyFingerprint) VALUES ('Core', 'host-concurrent', '2026-08-16 00:00:00', 1, 10.0, 'key-old');";
            await cmd.ExecuteNonQueryAsync();
        };

        var report = new HeartbeatReport(8, 25.5);
        await store.UpsertAsync("Core", "host-concurrent", report, "key-new", HeartbeatChannel.Push, CancellationToken.None);

        using var scope = _provider.CreateScope();
        var verifyDbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var rows = await verifyDbContext.HostHeartbeats.AsNoTracking()
            .Where(h => h.Role == "Core" && h.MachineName == "host-concurrent")
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(8, row.OutboxPending);
        Assert.Equal(25.5, row.OutboxOldestAgeSeconds);
        Assert.Equal("key-new", row.EncryptionKeyFingerprint);
    }

    [Fact]
    public async Task UpsertAsync_PersistsChannelAndOverwritesOnDirectionChange()
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var store = CreateStore(dbContext);

        await store.UpsertAsync("Edge", "edge-1", new HeartbeatReport(1, 2), null, HeartbeatChannel.Push, CancellationToken.None);
        var pushed = await dbContext.HostHeartbeats.AsNoTracking().SingleAsync(h => h.MachineName == "edge-1");
        Assert.Equal(HeartbeatChannel.Push, pushed.Channel);

        // 推送不通改由 Core 輪詢之後，同一列要反映新的方向
        await store.UpsertAsync("Edge", "edge-1", new HeartbeatReport(1, 2), null, HeartbeatChannel.Pull, CancellationToken.None);
        var pulled = await dbContext.HostHeartbeats.AsNoTracking().SingleAsync(h => h.MachineName == "edge-1");
        Assert.Equal(HeartbeatChannel.Pull, pulled.Channel);
    }
}
