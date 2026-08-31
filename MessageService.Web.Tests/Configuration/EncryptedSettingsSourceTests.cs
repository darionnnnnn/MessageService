using System.Security.Cryptography;
using MessageService.Options;
using MessageService.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

public class EncryptedSettingsSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"messageservice-settings-test-{Guid.NewGuid():N}");

    public EncryptedSettingsSourceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NonExistentFile_ReturnsEmptyDictionary_FallsBackToAppSettings()
    {
        var path = Path.Combine(_tempDir, "edge-settings.dat");
        var protector = new PlaintextSettingsProtector();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Line:ChannelSecret"] = "initial-secret",
                ["Line:ChannelAccessToken"] = "initial-token"
            })
            .Add(new EncryptedSettingsConfigurationSource(path, protector))
            .Build();

        Assert.Equal("initial-secret", config["Line:ChannelSecret"]);
        Assert.Equal("initial-token", config["Line:ChannelAccessToken"]);
    }

    [Fact]
    public void WriteSettings_ThenReload_IOptionsMonitorUpdatesImmediatelyWithoutRebuildingHost()
    {
        var path = Path.Combine(_tempDir, "edge-settings.dat");
        var protector = new PlaintextSettingsProtector();
        var source = new EncryptedSettingsConfigurationSource(path, protector);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Line:ChannelSecret"] = "old-secret"
            })
            .Add(source)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<LineOptions>(config.GetSection(LineOptions.SectionName));
        using var sp = services.BuildServiceProvider();

        var monitor = sp.GetRequiredService<IOptionsMonitor<LineOptions>>();
        Assert.Equal("old-secret", monitor.CurrentValue.ChannelSecret);

        // 寫入加密設定檔並 Reload
        var store = new EdgeSettingsStore(path, protector, source.Provider, NullLogger<EdgeSettingsStore>.Instance);
        store.Save(new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "new-secret"
        });

        // 不重建 host，IOptionsMonitor 立即拿到新值
        Assert.Equal("new-secret", monitor.CurrentValue.ChannelSecret);
    }

    [Fact]
    public void MissingKeyInEncryptedSettings_FallsBackToAppSettings()
    {
        var path = Path.Combine(_tempDir, "edge-settings.dat");
        var protector = new PlaintextSettingsProtector();
        var source = new EncryptedSettingsConfigurationSource(path, protector);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Line:ChannelSecret"] = "appsettings-secret",
                ["Line:ChannelAccessToken"] = "appsettings-token"
            })
            .Add(source)
            .Build();

        var store = new EdgeSettingsStore(path, protector, source.Provider, NullLogger<EdgeSettingsStore>.Instance);
        store.Save(new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "override-secret"
            // Line:ChannelAccessToken 未在加密檔提供
        });

        Assert.Equal("override-secret", config["Line:ChannelSecret"]);
        Assert.Equal("appsettings-token", config["Line:ChannelAccessToken"]);
    }

    [Fact]
    public void CorruptedFile_DoesNotThrow_FallsBackToAppSettings()
    {
        var path = Path.Combine(_tempDir, "edge-settings.dat");
        File.WriteAllBytes(path, [0xFF, 0xFE, 0x00, 0x12, 0x34, 0x56, 0x78, 0x9A]); // 毀損內容

        var protector = new PlaintextSettingsProtector();
        var source = new EncryptedSettingsConfigurationSource(path, protector, NullLogger.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Line:ChannelSecret"] = "fallback-secret"
            })
            .Add(source)
            .Build();

        Assert.Equal("fallback-secret", config["Line:ChannelSecret"]);
    }

    [Fact]
    public void AtomicWrite_CreatesFileAndCanBeReadBackCorrectly()
    {
        var path = Path.Combine(_tempDir, "edge-settings.dat");
        var protector = new PlaintextSettingsProtector();

        var values = new Dictionary<string, string?>
        {
            ["Ingest:ApiKey"] = "secret-123",
            ["Ingest:AllowedClientIps:0"] = "192.0.2.1"
        };

        EncryptedSettingsFile.Write(path, values, protector);

        Assert.True(File.Exists(path));
        var readBack = EncryptedSettingsFile.Read(path, protector);

        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, readBack.Status);
        Assert.Equal("secret-123", readBack.Values["Ingest:ApiKey"]);
        Assert.Equal("192.0.2.1", readBack.Values["Ingest:AllowedClientIps:0"]);

        // 不應殘留任何 .tmp 檔案
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void Read_NonExistentFile_ReturnsNotFoundStatusAndEmptyDictionary()
    {
        var path = Path.Combine(_tempDir, "non-existent.dat");
        var protector = new PlaintextSettingsProtector();

        var result = EncryptedSettingsFile.Read(path, protector);

        Assert.Equal(EncryptedSettingsLoadStatus.NotFound, result.Status);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void Read_ValidFile_ReturnsLoadedStatusAndCorrectValues()
    {
        var path = Path.Combine(_tempDir, "valid-settings.dat");
        var protector = new PlaintextSettingsProtector();
        var data = new Dictionary<string, string?>
        {
            ["Line:ChannelSecret"] = "secret-abc",
            ["Ingest:ApiKey"] = "key-xyz"
        };

        EncryptedSettingsFile.Write(path, data, protector);
        var result = EncryptedSettingsFile.Read(path, protector);

        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, result.Status);
        Assert.Equal("secret-abc", result.Values["Line:ChannelSecret"]);
        Assert.Equal("key-xyz", result.Values["Ingest:ApiKey"]);
    }

    [Fact]
    public void Read_CorruptedFile_ReturnsUnreadableStatusAndEmptyDictionary()
    {
        var path = Path.Combine(_tempDir, "corrupted.dat");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);
        var protector = new PlaintextSettingsProtector();

        var result = EncryptedSettingsFile.Read(path, protector);

        Assert.Equal(EncryptedSettingsLoadStatus.Unreadable, result.Status);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void Provider_And_Store_ReflectLoadStatusAccurately()
    {
        var path = Path.Combine(_tempDir, "status-check.dat");
        var protector = new PlaintextSettingsProtector();
        var source = new EncryptedSettingsConfigurationSource(path, protector);
        // provider 帶著監看這個暫存目錄的 FileSystemWatcher，不釋放的話它會活到測試把目錄
        // 刪掉之後，在收尾期對著已消失的目錄觸發重載
        using var provider = source.Provider;

        // 尚未建檔前載入：NotFound
        provider.Load();
        Assert.Equal(EncryptedSettingsLoadStatus.NotFound, provider.LoadStatus);

        var store = new EdgeSettingsStore(path, protector, provider, NullLogger<EdgeSettingsStore>.Instance);
        Assert.Equal(EncryptedSettingsLoadStatus.NotFound, store.LoadStatus);

        // 正常寫入後：Loaded
        store.Save(new Dictionary<string, string?> { ["Key"] = "Value" });
        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, provider.LoadStatus);
        Assert.Equal(EncryptedSettingsLoadStatus.Loaded, store.LoadStatus);

        // 破壞檔案後手動呼叫 Reload：Unreadable
        File.WriteAllBytes(path, [0xFF, 0xFF, 0xFF]);
        store.Reload();
        Assert.Equal(EncryptedSettingsLoadStatus.Unreadable, provider.LoadStatus);
        Assert.Equal(EncryptedSettingsLoadStatus.Unreadable, store.LoadStatus);
    }
}
