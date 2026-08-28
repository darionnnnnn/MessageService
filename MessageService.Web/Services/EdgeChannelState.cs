using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Edge 端的通道狀態：edge→core 這個方向現在通不通，以及還要不要再試。
///
/// <list type="bullet">
/// <item><c>Push</c>：永遠嘗試推送，失敗就照 outbox 既有的退避重試，與過去行為相同。</item>
/// <item><c>Pull</c>：從不主動連 Core（推送相關的背景服務根本不註冊），這個型別在該模式下
/// 一律回報「不可推送」，不做任何探測——明知防火牆封死的環境不該每秒空試。</item>
/// <item><c>Auto</c>：推送優先；推送失敗後暫停轉發，之後固定每
/// <c>Ingest:ChannelProbeIntervalMinutes</c>（預設 60 分）放行一次當作探測，
/// 一旦成功就恢復正常推送。探測期間 Core 端的輪詢照常把資料取走，資料不會遺失，
/// 探測頻率只影響「防火牆重新開通後多久升級回推送」。</item>
/// </list>
///
/// 刻意不做指數退避：退避的意義是「對方可能馬上就好」，但這裡要處理的是「防火牆這一段
/// 根本沒開通」，每秒重試只是白費——固定週期探測既能自動升級，又不會製造無謂連線。</summary>
public class EdgeChannelState(IOptions<IngestOptions> options, TimeProvider timeProvider)
{
    private readonly IngestChannel _channel = options.Value.Channel;
    private readonly TimeSpan _probeInterval = TimeSpan.FromMinutes(
        Math.Max(1, options.Value.ChannelProbeIntervalMinutes));

    private readonly object _syncLock = new();

    /// <summary>null＝推送健康；有值＝上次探測（或失敗）的時刻，下次探測要等一個週期。</summary>
    private DateTimeOffset? _pushPausedSince;

    /// <summary>這個模式下推送通道是否存在。Pull 模式永遠 false。</summary>
    public bool PushConfigured => _channel is not IngestChannel.Pull;

    /// <summary>目前是否處於「推送暫停」狀態（只用來讓狀態轉換各記一次 log）。</summary>
    public bool PushPaused
    {
        get
        {
            lock (_syncLock)
            {
                return _pushPausedSince is not null;
            }
        }
    }

    /// <summary>現在可不可以送一批出去：健康時永遠可以；暫停中只有探測週期到了才放行一次。</summary>
    public bool ShouldAttemptPush()
    {
        if (!PushConfigured)
        {
            return false;
        }

        lock (_syncLock)
        {
            if (_pushPausedSince is not { } pausedSince)
            {
                return true;
            }

            if (timeProvider.GetUtcNow() - pausedSince < _probeInterval)
            {
                return false;
            }

            // 放行這一次當探測：先把計時往後推，避免探測失敗時同一個週期內被連續放行
            _pushPausedSince = timeProvider.GetUtcNow();
            return true;
        }
    }

    public void MarkPushSucceeded()
    {
        lock (_syncLock)
        {
            _pushPausedSince = null;
        }
    }

    public void MarkPushFailed()
    {
        lock (_syncLock)
        {
            _pushPausedSince ??= timeProvider.GetUtcNow();
        }
    }
}
