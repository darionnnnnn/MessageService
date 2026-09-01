using Microsoft.Extensions.Logging;

namespace MessageService.Web.Diagnostics;

/// <summary>
/// 記憶體環形緩衝的單筆日誌記錄（不可變型別）。
/// </summary>
public sealed record LogBufferEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionSummary);
