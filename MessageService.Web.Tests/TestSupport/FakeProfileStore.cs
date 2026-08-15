using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeProfileStore : IProfileStore
{
    public ProfileStaleness StalenessToReturn { get; set; } = new(false, false);
    public List<(string GroupId, string? UserId, DateTimeOffset Cutoff)> GetStalenessCalls { get; } = [];
    public List<(string GroupId, GroupSummary Summary)> UpsertedGroups { get; } = [];
    public List<(string GroupId, string UserId, MemberProfile Profile)> UpsertedMembers { get; } = [];

    public Task<ProfileStaleness> GetStalenessAsync(string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        GetStalenessCalls.Add((groupId, userId, cutoff));
        return Task.FromResult(StalenessToReturn);
    }

    public Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
    {
        UpsertedGroups.Add((groupId, summary));
        return Task.CompletedTask;
    }

    public Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
    {
        UpsertedMembers.Add((groupId, userId, profile));
        return Task.CompletedTask;
    }
}
