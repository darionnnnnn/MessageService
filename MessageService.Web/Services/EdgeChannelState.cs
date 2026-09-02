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
public class EdgeChannelState(
    IOptions<DeploymentOptions> deploymentOptions, IOptions<IngestOptions> options, TimeProvider timeProvider)
{
    /// <summary>只有 Edge 有「推送方向」可言。AllInOne 的 sink 是 DirectIngestSink，落地失敗
    /// 是資料庫的問題，沿用 outbox 既有的秒級退避即可，不該被這裡的通道閘門擋住。</summary>
    private readonly bool _isEdge = deploymentOptions.Value.Mode is DeploymentMode.Edge;

    private readonly IngestChannel _channel = options.Value.Channel;
    private readonly TimeSpan _probeInterval = TimeSpan.FromMinutes(
        Math.Max(1, options.Value.ChannelProbeIntervalMinutes));

    /// <summary>推送失敗多久之後才真的暫停。短暫失敗（Core 重啟、IIS 回收、網路抖動）沿用
    /// outbox 既有的秒級退避快速恢復，不該讓 Edge 停推一小時——尤其純推送部署沒有輪詢器接手。
    /// 用與 Core 端接手門檻相同的 PullActivationSeconds：對方開始輪詢的同一時點，這邊才停推。
    ///
    /// 失敗訊號有兩個來源：outbox 批次推送（只在有訊息時才會嘗試）與心跳（每個週期固定送，
    /// 見 HttpHeartbeatReporter）。**兩者都要**——只靠 outbox 的話，安靜的站台沒有訊息流量就
    /// 永遠不會進入暫停，媒體與名稱／頭貼會一直打向不通的 Core。</summary>
    private readonly TimeSpan _pauseAfter = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.PullActivationSeconds));

    private readonly object _syncLock = new();

    /// <summary>第一次連續失敗的時刻。超過 _pauseAfter 才進入暫停狀態。</summary>
    private DateTimeOffset? _failingSince;

    /// <summary>null＝推送尚未暫停；有值＝上次探測（或進入暫停）的時刻，下次探測要等一個週期。</summary>
    private DateTimeOffset? _pushPausedSince;

    /// <summary>這個模式下推送通道是否存在。Pull 模式永遠 false。</summary>
    public bool PushConfigured => !_isEdge || _channel is not IngestChannel.Pull;

    /// <summary>現在該不該改用「等 Core 來拿」的那組資源（記憶體暫存、Core 派下來的 staleness）。
    /// Pull 永遠是；Auto 在推送暫停期間是——否則媒體與名稱／頭貼會繼續往打不通的 Core 送，
    /// 只有訊息與心跳反轉、其他兩條流靜默失效。</summary>
    public bool UsePullResources => _isEdge && (_channel is IngestChannel.Pull || PushPaused);

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
    /// <summary>呼叫端保證這時手上有一批要送的東西（閘門在取完非空批次之後才問）——
    /// 暫停中放行即視為消耗一次探測。</summary>
    public bool ShouldAttemptPush()
    {
        if (!PushConfigured)
        {
            return false;
        }

        if (!_isEdge)
        {
            return true;
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
        if (!_isEdge)
        {
            return;
        }

        lock (_syncLock)
        {
            _failingSince = null;
            _pushPausedSince = null;
        }
    }

    public void MarkPushFailed()
    {
        if (!_isEdge)
        {
            return;
        }

        lock (_syncLock)
        {
            var now = timeProvider.GetUtcNow();
            _failingSince ??= now;

            // 寬限期內先讓 outbox 自己的秒級退避處理，行為與沒有這個機制時相同
            if (now - _failingSince.Value >= _pauseAfter)
            {
                _pushPausedSince ??= now;
            }
        }
    }
}
