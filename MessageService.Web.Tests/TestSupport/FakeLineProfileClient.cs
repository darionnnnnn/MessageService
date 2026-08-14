using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeLineProfileClient : ILineProfileClient
{
    public List<string> GroupSummaryCalls { get; } = [];
    public List<(string GroupId, string UserId)> MemberProfileCalls { get; } = [];

    public Func<string, GroupSummary?> OnGetGroupSummary { get; set; } =
        groupId => new GroupSummary(groupId, "Test Group", null);

    public Func<string, string, MemberProfile?> OnGetGroupMemberProfile { get; set; } =
        (_, userId) => new MemberProfile(userId, "Test User", null);

    public Task<GroupSummary?> GetGroupSummaryAsync(string groupId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        GroupSummaryCalls.Add(groupId);
        return Task.FromResult(OnGetGroupSummary(groupId));
    }

    public Task<MemberProfile?> GetGroupMemberProfileAsync(string groupId, string userId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken)
    {
        MemberProfileCalls.Add((groupId, userId));
        return Task.FromResult(OnGetGroupMemberProfile(groupId, userId));
    }
}
