namespace MessageService.Options;

public class LineOptions
{
    public const string SectionName = "Line";

    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";
}
