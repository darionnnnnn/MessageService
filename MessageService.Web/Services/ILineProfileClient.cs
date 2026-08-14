namespace MessageService.Services;

public interface ILineProfileClient
{
    /// <summary>Null if the group summary is unavailable (e.g. bot no longer in the group).</summary>
    Task<GroupSummary?> GetGroupSummaryAsync(string groupId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken);

    /// <summary>Null if the member profile is unavailable (e.g. member left, privacy settings).</summary>
    Task<MemberProfile?> GetGroupMemberProfileAsync(string groupId, string userId, string? knownPictureUrl, bool hasPicture, CancellationToken cancellationToken);
}
