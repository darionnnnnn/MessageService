namespace MessageService.Models;

public class GroupPicture
{
    public required string GroupId { get; set; }
    public required byte[] Content { get; set; }

    public Group? Group { get; set; }
}
