namespace MessageService.Options;

public class RetentionOptions
{
    public const string SectionName = "Retention";

    public int Years { get; set; } = 3;
    public TimeSpan CleanupTimeOfDay { get; set; } = TimeSpan.FromHours(3);
}
