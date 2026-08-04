using System.Text.Json.Serialization;

namespace MessageService.Models.Line;

public class LineMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("stickerId")]
    public string? StickerId { get; set; }

    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }
}
