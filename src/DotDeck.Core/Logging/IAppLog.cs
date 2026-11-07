namespace DotDeck.Core.Logging;

public enum AppLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message, string? Exception);

/// <summary>
/// Application log. Implementations must mask secrets and never throw from <see cref="Write"/>.
/// File contents and environment variable values must not be passed in messages.
/// </summary>
public interface IAppLog
{
    /// <summary>Raised after an entry is recorded (output pane subscribes). May fire on any thread.</summary>
    event EventHandler<AppLogEntry>? EntryWritten;

    void Write(AppLogLevel level, string message, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string message) => log.Write(AppLogLevel.Debug, message);

    public static void Info(this IAppLog log, string message) => log.Write(AppLogLevel.Info, message);

    public static void Warning(this IAppLog log, string message, Exception? exception = null) =>
        log.Write(AppLogLevel.Warning, message, exception);

    public static void Error(this IAppLog log, string message, Exception? exception = null) =>
        log.Write(AppLogLevel.Error, message, exception);
}
