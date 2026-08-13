namespace MessageService.Web.Dtos;

/// <summary>目前實際生效的資料庫 provider 與 SQLite 救場狀態（見 DatabaseStartupDecision）——
/// 只有本機這台主機的狀態，不是跨主機彙整，見 SettingsController.GetDatabaseStatus。</summary>
public record DatabaseStatusDto(string EffectiveProvider, bool SqliteFallbackActive, string? SqliteFallbackReason);
