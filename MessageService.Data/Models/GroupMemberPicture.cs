namespace MessageService.Models;

public class GroupMemberPicture
{
    public required string GroupId { get; set; }
    public required string UserId { get; set; }
    public required byte[] Content { get; set; }

    public GroupMember? GroupMember { get; set; }
}
