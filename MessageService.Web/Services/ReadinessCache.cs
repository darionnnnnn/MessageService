namespace MessageService.Web.Services;

/// <summary>
/// 就緒探針（/healthz/ready）探測結果的 5 秒記憶體快取。
///
/// 快取原因：
/// /healthz/ready 端點為配合監控系統運作而刻意排除於 IP 白名單之外，是全站唯一一個不需任何憑證即可觸發
/// 資料庫連線動作的入口。每次請求若都對 SQL Server 建立真實連線探測，在監控系統高頻輪詢下會對連線池造成
/// 不必要的負擔。透過將探測結果快取 5 秒，監控輪詢間隔低於 5 秒時亦不會增加資料庫連線壓力。
///
/// 失敗結果亦快取：
/// 當資料庫故障或無法連線時，若每次探測仍持續打向資料庫發起連線，快取機制將失去保護效果；因此無論探測結果
/// 為成功（true）或失敗（false），皆快取 5 秒。
///
/// 並行取捨：
/// 僅在讀取與寫入內部快取欄位時使用 lock 同步保護，絕不將非同步探測（await probe(...)）包在鎖內。
/// 當快取過期且多個請求同時進入時，偶爾各自執行一次探測是完全可接受的；若為此引入 SemaphoreSlim 進行
/// 排隊等待反而屬於過度設計。
/// </summary>
public class ReadinessCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly object _syncLock = new();

    private bool _hasValue;
    private bool _cachedResult;
    private DateTimeOffset _expiresAtUtc;

    public async Task<bool> IsReadyAsync(
        Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_syncLock)
        {
            if (_hasValue && now < _expiresAtUtc)
            {
                return _cachedResult;
            }
        }

        var result = await probe(cancellationToken);

        var nextExpiresAtUtc = timeProvider.GetUtcNow() + Ttl;
        lock (_syncLock)
        {
            _cachedResult = result;
            _expiresAtUtc = nextExpiresAtUtc;
            _hasValue = true;
        }

        return result;
    }
}
