namespace MessageService.Services;

/// <summary>Edge 端的名稱／頭貼資料來源，依目前通道方向二選一——理由與
/// <see cref="ChannelAwareContentWorkSource"/> 相同。</summary>
public class ChannelAwareProfileStore(
    EdgeChannelState channelState,
    StagingProfileStore staging,
    IServiceProvider serviceProvider) : IProfileStore
{
    private IProfileStore Active => channelState.UsePullResources
        ? staging
        : serviceProvider.GetRequiredService<ApiProfileStore>();

    public Task<ProfileStaleness> GetStalenessAsync(
        string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        Active.GetStalenessAsync(groupId, userId, cutoff, cancellationToken);

    public Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken) =>
        Active.UpsertGroupAsync(groupId, summary, cancellationToken);

    public Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken) =>
        Active.UpsertMemberAsync(groupId, userId, profile, cancellationToken);
}
