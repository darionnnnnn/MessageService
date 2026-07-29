namespace MessageService.Options;

public class ContentDownloadOptions
{
    public const string SectionName = "ContentDownload";

    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public int TranscodingPollSeconds { get; set; } = 5;
    public int TranscodingMaxPolls { get; set; } = 24;
}
