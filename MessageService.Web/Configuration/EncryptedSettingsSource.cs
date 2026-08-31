using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Configuration;

/// <summary>
/// 設定值加解密介面。
/// 抽換介面可使測試不需綁定 Windows DPAPI。
/// </summary>
public interface ISettingsProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// 使用 Windows DPAPI（ProtectedData 搭配 DataProtectionScope.LocalMachine）加解密 Edge 設定。
///
/// 為什麼是 raw DPAPI 而不是 ASP.NET Core 的 DataProtection：
/// Edge 主機上 DataProtection 的金鑰環是 ephemeral（實機 log 已出現
/// 「Using an in-memory repository. Keys will not be persisted to storage.」與
/// 「No XML encryptor configured」兩則警告），重啟後解不開自己寫的檔案。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class DpapiSettingsProtector : ISettingsProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, null, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.LocalMachine);
    }
}

/// <summary>
/// 明文替身加解密實作，供單元測試使用。
/// </summary>
public class PlaintextSettingsProtector : ISettingsProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return (byte[])plaintext.Clone();
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return (byte[])ciphertext.Clone();
    }
}

/// <summary>
/// 負責解析路徑、讀取與不可分割寫入 Edge 加密設定檔的小工具。
/// </summary>
public static class EncryptedSettingsFile
{
    /// <summary>
    /// 回傳 Db/edge-settings.dat 的絕對路徑。
    /// 路徑解析以 ContentRootPath 為基準，比照既有的連線字串目錄慣例。
    /// </summary>
    public static string ResolvePath(string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(contentRootPath);
        return Path.GetFullPath(Path.Combine(contentRootPath, "Db", "edge-settings.dat"));
    }

    /// <summary>
    /// 讀取加密設定檔。
    /// 檔案不存在時回傳空字典（不拋例外）；
    /// 解密或反序列化失敗時回傳空字典並記錄警告（避免設定損毀導致站台無法啟動，退回 appsettings）。
    /// </summary>
    public static IDictionary<string, string?> Read(string path, ISettingsProtector protector, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(protector);

        if (!File.Exists(path))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var ciphertext = File.ReadAllBytes(path);
            if (ciphertext.Length == 0)
            {
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }

            var plaintext = protector.Unprotect(ciphertext);
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(plaintext);
            return values is not null
                ? new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "讀取或解密 Edge 加密設定檔失敗：{Path}，將退回使用 appsettings 設定值", path);
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 序列化並加密寫入設定檔。
    /// 先寫入同目錄下的暫存檔再使用 File.Move 覆蓋，避免寫到一半斷電留下半截檔。
    /// </summary>
    public static void Write(string path, IDictionary<string, string?> values, ISettingsProtector protector)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(protector);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(values);
        var ciphertext = protector.Protect(json);

        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllBytes(tempPath, ciphertext);
        File.Move(tempPath, path, overwrite: true);
    }
}

/// <summary>
/// 加密設定檔的 ConfigurationProvider。
/// </summary>
public class EncryptedSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly string _path;
    private ISettingsProtector _protector;
    private readonly ILogger? _logger;

    public EncryptedSettingsConfigurationProvider(string path, ISettingsProtector protector, ILogger? logger = null)
    {
        _path = path;
        _protector = protector;
        _logger = logger;
    }

    public void SetProtector(ISettingsProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public override void Load()
    {
        Data = new Dictionary<string, string?>(
            EncryptedSettingsFile.Read(_path, _protector, _logger),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 重讀設定檔並觸發 change token，通知相依的 IOptionsMonitor 即時更新。
    /// </summary>
    public void Reload()
    {
        Load();
        OnReload();
    }
}

/// <summary>
/// 加密設定檔的 IConfigurationSource。
/// </summary>
public class EncryptedSettingsConfigurationSource : IConfigurationSource
{
    private readonly string _path;
    private readonly ISettingsProtector _protector;
    private readonly ILogger? _logger;
    private EncryptedSettingsConfigurationProvider? _provider;

    public EncryptedSettingsConfigurationSource(string path, ISettingsProtector protector, ILogger? logger = null)
    {
        _path = path;
        _protector = protector;
        _logger = logger;
    }

    public EncryptedSettingsConfigurationProvider Provider =>
        _provider ??= new EncryptedSettingsConfigurationProvider(_path, _protector, _logger);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>
/// 提供 Edge 端設定的讀取、儲存與熱生效功能。
/// 寫入加密設定檔後會立即通知 EncryptedSettingsConfigurationProvider 重讀並觸發設定變更通知。
/// </summary>
public class EdgeSettingsStore
{
    private readonly string _path;
    private readonly ISettingsProtector _protector;
    private readonly EncryptedSettingsConfigurationProvider? _provider;
    private readonly ILogger<EdgeSettingsStore> _logger;

    public EdgeSettingsStore(
        string path,
        ISettingsProtector protector,
        EncryptedSettingsConfigurationProvider? provider,
        ILogger<EdgeSettingsStore> logger)
    {
        _path = path;
        _protector = protector;
        _provider = provider;
        _logger = logger;
        if (_provider is not null)
        {
            _provider.SetProtector(protector);
        }
    }

    public string Path => _path;

    public IDictionary<string, string?> Read()
    {
        return EncryptedSettingsFile.Read(_path, _protector, _logger);
    }

    public void Save(IDictionary<string, string?> values)
    {
        EncryptedSettingsFile.Write(_path, values, _protector);
        _provider?.Reload();
    }

    public void Reload()
    {
        _provider?.Reload();
    }
}