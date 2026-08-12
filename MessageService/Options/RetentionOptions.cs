namespace MessageService.Options;

/// <summary>保留天數本身改存 ViewerSettings.RetentionDays（設定頁可調，見 SettingsController），
/// 這裡只留下跟「什麼時候跑」有關的排程設定——那不是使用者會頻繁調整的東西，沒必要搬進 DB。</summary>
public class RetentionOptions
{
    public const string SectionName = "Retention";

    public TimeSpan CleanupTimeOfDay { get; set; } = TimeSpan.FromHours(3);
}
