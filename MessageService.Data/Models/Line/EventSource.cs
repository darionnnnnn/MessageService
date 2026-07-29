using System.Text.Json.Serialization;

namespace MessageService.Models.Line;

public class EventSource
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }
}
