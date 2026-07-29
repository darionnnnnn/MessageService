using System.Text.Json.Serialization;

namespace MessageService.Models.Line;

public class WebhookRequest
{
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("events")]
    public List<WebhookEvent> Events { get; set; } = [];
}
