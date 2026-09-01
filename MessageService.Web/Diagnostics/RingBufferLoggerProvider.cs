using Microsoft.Extensions.Logging;

namespace MessageService.Web.Diagnostics;

/// <summary>
/// 記憶體環形緩衝日誌提供者。
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;

    public RingBufferLoggerProvider(LogRingBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RingBufferLogger(categoryName, _buffer);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 記憶體環形緩衝日誌記錄器，僅記錄 Warning 等級以上的訊息。
/// </summary>
public sealed class RingBufferLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogRingBuffer _buffer;

    public RingBufferLogger(string categoryName, LogRingBuffer buffer)
    {
        _categoryName = categoryName ?? string.Empty;
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel is LogLevel.Warning or LogLevel.Error or LogLevel.Critical;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        var entry = new LogBufferEntry(
            DateTimeOffset.UtcNow,
            logLevel,
            _categoryName,
            message,
            FormatExceptionSummary(exception));

        _buffer.Add(entry);
    }

    internal static string? FormatExceptionSummary(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        var header = $"{typeName}: {exception.Message}";

        if (string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            return header;
        }

        var firstStackLine = exception.StackTrace
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        return string.IsNullOrEmpty(firstStackLine)
            ? header
            : $"{header}\n{firstStackLine}";
    }
}
