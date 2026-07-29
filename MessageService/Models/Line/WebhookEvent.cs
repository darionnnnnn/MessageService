using System.Text.Json.Serialization;

namespace MessageService.Models.Line;

public class WebhookEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("webhookEventId")]
    public string? WebhookEventId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("source")]
    public EventSource? Source { get; set; }

    [JsonPropertyName("message")]
    public LineMessage? Message { get; set; }
}
