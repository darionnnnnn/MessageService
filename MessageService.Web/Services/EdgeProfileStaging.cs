using System.Collections.Concurrent;

namespace MessageService.Services;

/// <summary>拉取模式下 Edge 端的名稱／頭貼刷新暫存區。
///
/// 方向與推送模式相反：Edge 沒有資料庫，判斷「這筆過期了沒」的 staleness 由 Core 在 poll
/// 請求裡一併派下來（<see cref="Dispatch"/>），Edge 打完 LINE API 之後把結果放進這裡
/// （<see cref="EnqueueGroup"/>／<see cref="EnqueueMember"/>），下一次 poll 回應帶回 Core 落地。
///
/// 每輪回傳量有位元組預算：頭貼上限 2MB，而 poll 走的是短逾時的小 JSON 通道，
/// 一次塞太多張會拖垮每秒一次的輪詢節奏。超出預算的留到下一輪，反正 TTL 是以天計的。</summary>
public class EdgeProfileStaging
{
    /// <summary>單輪回應的名稱／頭貼結果位元組預算。取 1MB：頭貼單張上限 2MB，
    /// 這個預算保證「至少送得出一筆」（見 DrainResults 的第一筆一定收下），
    /// 又不會讓一次 poll 變成好幾 MB 的大回應。</summary>
    public const long ResultBudgetBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<string, ProfileStaleness> _staleness = new();
    private readonly ConcurrentQueue<EdgeProfileResult> _results = new();

    private static string Key(string groupId, string? userId) => userId is null ? groupId : $"{groupId}:{userId}";

    /// <summary>收下 Core 派來的刷新工作與它算好的 staleness。</summary>
    public void Dispatch(IReadOnlyList<EdgeProfileWorkItem> items)
    {
        foreach (var item in items)
        {
            _staleness[Key(item.GroupId, item.UserId)] = item.Staleness;
        }
    }

    /// <summary>取出 Core 派下來的 staleness。查無代表 Core 沒派這筆——一律回報「不過期」，
    /// 讓 Edge 不要多打 LINE API（沒被派工的東西本來就不該刷新）。</summary>
    public ProfileStaleness GetStaleness(string groupId, string? userId) =>
        _staleness.TryGetValue(Key(groupId, userId), out var staleness)
            ? staleness
            : new ProfileStaleness(GroupStale: false, MemberStale: false);

    public void EnqueueGroup(string groupId, GroupSummary summary)
    {
        _staleness.TryRemove(Key(groupId, null), out _);
        _results.Enqueue(new EdgeProfileResult(groupId, UserId: null, Group: summary, Member: null));
    }

    public void EnqueueMember(string groupId, string userId, MemberProfile profile)
    {
        _staleness.TryRemove(Key(groupId, userId), out _);
        _results.Enqueue(new EdgeProfileResult(groupId, userId, Group: null, Member: profile));
    }

    /// <summary>取出這一輪要回報的結果，總量不超過預算（第一筆一定收下，避免單一大頭貼
    /// 永遠卡在佇列前面誰也送不出去）。取出即移除：Core 落地失敗時那筆會因為 TTL 仍然過期
    /// 而在之後的輪次被重新派工，不需要在這裡做 ack。</summary>
    public IReadOnlyList<EdgeProfileResult> DrainResults()
    {
        var drained = new List<EdgeProfileResult>();
        long budget = 0;

        while (_results.TryPeek(out var next))
        {
            var size = EstimateSize(next);
            if (drained.Count > 0 && budget + size > ResultBudgetBytes)
            {
                break;
            }

            _results.TryDequeue(out var item);
            if (item is null)
            {
                break;
            }

            drained.Add(item);
            budget += size;
        }

        return drained;
    }

    private static long EstimateSize(EdgeProfileResult result) =>
        (result.Group?.PictureBytes?.LongLength ?? 0) + (result.Member?.PictureBytes?.LongLength ?? 0);
}

/// <summary>Core 派給 Edge 的一筆名稱／頭貼刷新工作。Staleness 由 Core 算好帶過來——
/// Edge 沒有資料庫，無從自己判斷。</summary>
public record EdgeProfileWorkItem(string GroupId, string? UserId, ProfileStaleness Staleness);

/// <summary>Edge 打完 LINE API 之後回報給 Core 的一筆結果。Group 與 Member 只會有一個有值。</summary>
public record EdgeProfileResult(string GroupId, string? UserId, GroupSummary? Group, MemberProfile? Member);
