using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>拉取模式下，Edge 端已下載完成、等 Core 來取走的媒體暫存區。
///
/// 走記憶體不落磁碟：Core 每秒輪詢一次，暫存的生命週期通常只有幾秒。代價是 Edge 重啟會遺失，
/// 但那不會掉資料——那些內容在 Core 端一路維持 Pending（派工不認領），下一輪 poll 就會
/// 重新派工，Edge 再下載一遍。
///
/// 總量上限由 Ingest:PullStagingMaxBytes 控制，滿了就拒收新派工（背壓）：讓工作留在 Core 端
/// 等下一輪，而不是把記憶體吃爆或丟掉已下載的內容。</summary>
public class EdgeContentStaging(IOptions<IngestOptions> options)
{
    /// <summary>實際生效的上限。夾到至少等於單一檔案上限——設得比 MaxContentBytes 還小時，
    /// 最大的那種檔案永遠塞不進去、只會不斷重新派工，那比多用一點記憶體糟得多。</summary>
    private readonly long _maxBytes = Math.Max(options.Value.PullStagingMaxBytes, options.Value.MaxContentBytes);
    private readonly object _syncLock = new();

    /// <summary>Core 已派工、還沒下載完的項目。key 是 MessageContent.Id。</summary>
    private readonly Dictionary<long, ContentWorkItem> _dispatched = [];

    /// <summary>已下載完成、等 Core 取走的 blob。</summary>
    private readonly Dictionary<long, StagedContent> _ready = [];

    /// <summary>下載失敗、要回報給 Core 的項目。</summary>
    private readonly HashSet<long> _failed = [];

    private long _stagedBytes;

    public record StagedContent(byte[] Content, string? ContentType);

    /// <summary>目前佔用的位元組數，供監看與測試斷言。</summary>
    public long StagedBytes
    {
        get
        {
            lock (_syncLock)
            {
                return _stagedBytes;
            }
        }
    }

    /// <summary>接受 Core 這一輪派下來的工作。回傳實際收下的項目 Id——暫存已滿時回傳的清單
    /// 會比傳入的少，沒收下的那些留在 Core 端維持 Pending，下一輪再派（背壓，不掉資料）。
    ///
    /// 已經在派工中或已下載完成的 Id 直接視為收下但不重複處理（冪等）：poll 回應遺失時
    /// Core 會重派同一批，不能因此下載兩次。</summary>
    public IReadOnlyList<long> AcceptDispatch(IReadOnlyList<ContentWorkItem> items)
    {
        var accepted = new List<long>(items.Count);
        lock (_syncLock)
        {
            foreach (var item in items)
            {
                if (_dispatched.ContainsKey(item.ContentId) || _ready.ContainsKey(item.ContentId))
                {
                    accepted.Add(item.ContentId);
                    continue;
                }

                // 已經滿了就不再收新的——無法預知這筆多大，用「目前已佔用量是否觸頂」當閘門
                if (_stagedBytes >= _maxBytes)
                {
                    continue;
                }

                _dispatched[item.ContentId] = item;
                accepted.Add(item.ContentId);
            }
        }
        return accepted;
    }

    /// <summary>目前待下載的項目 Id（給 ContentDownloadService 的 work source 用）。</summary>
    public IReadOnlyList<long> GetPendingIds()
    {
        lock (_syncLock)
        {
            return [.. _dispatched.Keys];
        }
    }

    public ContentWorkItem? GetDispatched(long contentId)
    {
        lock (_syncLock)
        {
            return _dispatched.GetValueOrDefault(contentId);
        }
    }

    /// <summary>下載完成，把內容放進暫存等 Core 來取。超過總量上限時回傳 false 並丟棄這份內容——
    /// 該筆會留在 Core 端由租約逾期回收重派，不會靜默消失。</summary>
    public bool TryStage(long contentId, byte[] content, string? contentType)
    {
        lock (_syncLock)
        {
            if (!_dispatched.ContainsKey(contentId) && !_ready.ContainsKey(contentId))
            {
                // 沒派工過的內容不收——正常流程不會發生，防止未預期的來源塞爆記憶體
                return false;
            }

            var occupied = _stagedBytes;
            if (_ready.TryGetValue(contentId, out var existing))
            {
                // 重複下載同一筆（例如重派後又完成一次）：以新的取代，先扣掉舊的佔用量
                occupied -= existing.Content.LongLength;
            }

            if (occupied + content.LongLength > _maxBytes)
            {
                // 收不下：**派工要留在 _dispatched**，否則這筆永遠不會有人再下載它，
                // 而且會一路走到 FailAsync 去消耗 Core 端的正式重試次數
                return false;
            }

            _dispatched.Remove(contentId);
            _ready[contentId] = new StagedContent(content, contentType);
            _stagedBytes = occupied + content.LongLength;
            return true;
        }
    }

    /// <summary>下載失敗，記下來由下一次 poll 回報給 Core。</summary>
    public void MarkFailed(long contentId)
    {
        lock (_syncLock)
        {
            _dispatched.Remove(contentId);
            _failed.Add(contentId);
        }
    }

    /// <summary>已下載完成、可供 Core 取回的項目 Id。</summary>
    public IReadOnlyList<long> GetReadyIds()
    {
        lock (_syncLock)
        {
            return [.. _ready.Keys];
        }
    }

    /// <summary>取出並清空待回報的失敗清單——回報過就不再重複回報。</summary>
    public IReadOnlyList<long> DrainFailedIds()
    {
        lock (_syncLock)
        {
            var failed = _failed.ToArray();
            _failed.Clear();
            return failed;
        }
    }

    public StagedContent? Get(long contentId)
    {
        lock (_syncLock)
        {
            return _ready.GetValueOrDefault(contentId);
        }
    }

    /// <summary>Core 已經完整落地並確認，釋放這筆佔用的記憶體。
    /// 收到 ack 之前絕對不能釋放：Core 的取回可能中途斷掉，要能原樣重取。</summary>
    public bool Release(long contentId)
    {
        lock (_syncLock)
        {
            if (!_ready.Remove(contentId, out var staged))
            {
                return false;
            }

            _stagedBytes -= staged.Content.LongLength;
            return true;
        }
    }
}
