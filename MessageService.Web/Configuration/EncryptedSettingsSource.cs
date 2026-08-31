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
/// 加密設定檔的載入狀態。
/// </summary>
public enum EncryptedSettingsLoadStatus
{
    /// <summary>
    /// 設定檔不存在（首次使用或尚未建立設定檔時的正常狀態）。
    /// </summary>
    NotFound,

    /// <summary>
    /// 設定檔讀取與解密成功。
    /// </summary>
    Loaded,

    /// <summary>
    /// 設定檔存在但無法解密或反序列化（常見原因：主機更換或還原映像導致 DPAPI 解不開）。
    /// </summary>
    Unreadable
}

/// <summary>
/// 加密設定檔讀取結果，包含設定值字典與載入狀態。
/// </summary>
public record EncryptedSettingsReadResult(
    IDictionary<string, string?> Values,
    EncryptedSettingsLoadStatus Status);

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
    /// 檔案不存在時回傳空字典與 NotFound 狀態（不拋例外、不記 log）；
    /// 讀取成功時回傳值字典與 Loaded 狀態；
    /// 解密或反序列化失敗時回傳空字典與 Unreadable 狀態並記錄 Error（避免設定損毀導致站台無法啟動，退回 appsettings）。
    /// </summary>
    /// <summary>讀檔並對「檔案正被別人占用」重試幾次。
    ///
    /// 寫入端是「先寫暫存檔再 File.Move 覆蓋」，而檔案監看的重載跟其他行程的寫入完全沒有
    /// 協調——重載剛好撞上 Move 的那一瞬間會拿到共用違規。**不重試的話後果不是讀不到這一次，
    /// 而是整份設定被判成毀損**：生效值退回 appsettings（等於換金鑰的當下退回舊金鑰），
    /// 而觸發它的那個檔案事件已經用掉了，沒有第二次事件會來修正，這個行程就一直錯到下次
    /// 寫檔或重啟為止。解密／反序列化失敗不走這裡（那才是真的毀損）。</summary>
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    public static EncryptedSettingsReadResult Read(string path, ISettingsProtector protector, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(protector);

        if (!File.Exists(path))
        {
            return new EncryptedSettingsReadResult(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                EncryptedSettingsLoadStatus.NotFound);
        }

        try
        {
            var ciphertext = ReadAllBytesWithRetry(path);
            if (ciphertext.Length == 0)
            {
                logger?.LogError(
                    "讀取或解密 Edge 加密設定檔失敗（檔案為空）：{Path}。常見原因：主機更換或還原映像導致 DPAPI 解不開。復原方式：在設定頁重新填寫並存檔即可重建，目前將退回使用 appsettings 設定值。",
                    path);
                return new EncryptedSettingsReadResult(
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    EncryptedSettingsLoadStatus.Unreadable);
            }

            var plaintext = protector.Unprotect(ciphertext);
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(plaintext);
            if (values is null)
            {
                logger?.LogError(
                    "讀取或反序列化 Edge 加密設定檔失敗（反序列化為 null）：{Path}。常見原因：主機更換或還原映像導致 DPAPI 解不開。復原方式：在設定頁重新填寫並存檔即可重建，目前將退回使用 appsettings 設定值。",
                    path);
                return new EncryptedSettingsReadResult(
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                    EncryptedSettingsLoadStatus.Unreadable);
            }

            return new EncryptedSettingsReadResult(
                new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase),
                EncryptedSettingsLoadStatus.Loaded);
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "讀取或解密 Edge 加密設定檔失敗：{Path}。常見原因：主機更換或還原映像導致 DPAPI 解不開。復原方式：在設定頁重新填寫並存檔即可重建，目前將退回使用 appsettings 設定值。",
                path);
            return new EncryptedSettingsReadResult(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                EncryptedSettingsLoadStatus.Unreadable);
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
/// 支援 FileSystemWatcher 監看設定檔變更並即時去抖動熱重載。
/// </summary>
public class EncryptedSettingsConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly string _path;
    // volatile：watcher 在建構式就啟動，而 protector 可能稍後才被 SetProtector 換掉
    // （見 EdgeSettingsStore 建構式）——監看回呼是別的執行緒，要保證讀得到最新的那顆
    private volatile ISettingsProtector _protector;
    private readonly ILogger? _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    // volatile：Dispose 與監看回呼在不同執行緒，沒有它的話回呼可能讀到過期的 false，
    // 在 Dispose 之後又建一顆永遠不會被釋放的 debounce Timer
    private volatile bool _disposed;
    private const int DebounceDelayMs = 300;

    public EncryptedSettingsConfigurationProvider(string path, ISettingsProtector protector, ILogger? logger = null)
    {
        _path = path;
        _protector = protector;
        _logger = logger;
        InitializeWatcher();
    }

    /// <summary>
    /// 最後一次載入的狀態。
    /// </summary>
    public EncryptedSettingsLoadStatus LoadStatus { get; private set; } = EncryptedSettingsLoadStatus.NotFound;

    public void SetProtector(ISettingsProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public override void Load()
    {
        var result = EncryptedSettingsFile.Read(_path, _protector, _logger);
        LoadStatus = result.Status;
        Data = new Dictionary<string, string?>(result.Values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 重讀設定檔並觸發 change token，通知相依的 IOptionsMonitor 即時更新。
    /// </summary>
    public void Reload()
    {
        Load();
        OnReload();
    }

    private void InitializeWatcher()
    {
        try
        {
            var fullPath = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "初始化設定檔 FileSystemWatcher 失敗：{Path}，將無法即時監看檔案變更", _path);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (string.Equals(Path.GetFileName(e.FullPath), Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase))
            {
                TriggerDebouncedReload();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "處理設定檔 FileSystemWatcher 事件失敗：{Path}", _path);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (string.Equals(Path.GetFileName(e.FullPath), Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase))
            {
                TriggerDebouncedReload();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "處理設定檔 FileSystemWatcher 檔名變更事件失敗：{Path}", _path);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogWarning(e.GetException(), "設定檔 FileSystemWatcher 發生錯誤：{Path}", _path);
    }

    private void TriggerDebouncedReload()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_debounceTimer is null)
            {
                _debounceTimer = new Timer(OnDebounceTimerElapsed, null, DebounceDelayMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(DebounceDelayMs, Timeout.Infinite);
            }
        }
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        if (_disposed) return;
        try
        {
            Reload();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "設定檔變更後重載失敗：{Path}", _path);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // 先在鎖內標記已釋放再拆 Timer：反過來的話，事件執行緒可以在「Timer 已拆、
            // _disposed 還沒設 true」的縫隙裡進到鎖內、再建一顆新的 Timer，那顆沒人會釋放，
            // 300ms 後對著已經拆掉的 host 觸發重載
            lock (_lock)
            {
                _disposed = true;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }

            if (_watcher is not null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFileEvent;
                    _watcher.Changed -= OnFileEvent;
                    _watcher.Renamed -= OnFileRenamed;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
                catch
                {
                    // 釋放資源時忽略非預期例外
                }
                _watcher = null;
            }
        }
        _disposed = true;
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

    /// <summary>
    /// 目前的載入狀態。
    /// 有 provider 時回傳 provider 的最後載入狀態（實際生效的那份狀態）；
    /// provider 為 null 時就地讀取一次檔案取得狀態。
    /// </summary>
    public EncryptedSettingsLoadStatus LoadStatus =>
        _provider is not null
            ? _provider.LoadStatus
            : EncryptedSettingsFile.Read(_path, _protector, _logger).Status;

    public IDictionary<string, string?> Read()
    {
        return EncryptedSettingsFile.Read(_path, _protector, _logger).Values;
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