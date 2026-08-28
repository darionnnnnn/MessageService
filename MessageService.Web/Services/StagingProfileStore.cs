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
}
