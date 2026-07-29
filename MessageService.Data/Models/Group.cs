namespace MessageService.Models;

public class Group
{
    public required string GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? PictureUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
