namespace MessageService.Options;

public class ProfileCacheOptions
{
    public const string SectionName = "ProfileCache";

    public TimeSpan RefreshAfter { get; set; } = TimeSpan.FromDays(7);
}
