namespace MessageService.Services;

/// <summary>拉取模式下 Edge 端的名稱／頭貼資料來源：staleness 由 Core 派下來、
/// upsert 改成放進暫存等 Core 取走（見 <see cref="EdgeProfileStaging"/>）。
///
/// <see cref="ProfileRefreshService"/> 的流程（查 TTL → 打 LINE → 過期才 upsert）完全不變，
/// 換掉的只有「這幾個資料操作去哪裡做」——與 <see cref="ApiProfileStore"/> 是同一種切法。</summary>
public class StagingProfileStore(EdgeProfileStaging staging) : IProfileStore
{
    public Task<ProfileStaleness> GetStalenessAsync(
        string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        Task.FromResult(staging.GetStaleness(groupId, userId));

    public Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        staging.EnqueueGroup(groupId, summary);
        return Task.CompletedTask;
    }

    public Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        staging.EnqueueMember(groupId, userId, profile);
        return Task.CompletedTask;
    }

    /// <summary>拉取模式下 Edge 沒有資料庫，也不會主動連 Core（那正是這個模式存在的理由），
    /// 所以撈不到候選，回空清單。**這代表拉取拓撲目前沒有背景補刷**：Core 端
    /// EdgePullService 的待辦只由「剛落地的訊息」播種，安靜的群組仍然不會被刷新。
    /// 要補這個缺口得在 Core 端把 GetStaleProfilesAsync 的結果併進待辦；現況見 docs/DEPLOYMENT-MODES.md。</summary>
    public Task<IReadOnlyList<ProfileRefreshTask>> GetStaleProfilesAsync(
        int max, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProfileRefreshTask>>([]);
}
