namespace MessageService.Models;

public class GroupMember
{
    public required string GroupId { get; set; }
    public required string UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
