namespace MessageService.Services;

/// <summary>ProfileRefreshService 查 TTL、寫回快取的資料來源——直接查本機資料庫（Full／Db 模式，
/// DbProfileStore）或打遠端 ingest API（Line 模式且 Line:OutboundHere=true，ApiProfileStore）。
/// 頭貼快取是非關鍵資料：API 不通時該次刷新失敗即可（下一則訊息會重新入列），
/// 不像訊息落地需要 outbox 保護，所以呼叫端（ProfileRefreshService）遇到例外只記 log、
/// 不重試——這點跟 IIngestSink 的處置刻意不同。</summary>
public interface IProfileStore
{
    /// <summary>userId 為 null 時只判斷群組（MemberStale 恆為 false）。</summary>
    Task<ProfileStaleness> GetStalenessAsync(string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken);

    Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken);
}
