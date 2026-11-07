using System.Globalization;
using System.Text;
using DotDeck.Core.Security;

namespace DotDeck.Core.Logging;

/// <summary>
/// Appends masked log lines to <c>&lt;logsDir&gt;\dotdeck-yyyyMMdd.log</c> (UTF-8, no BOM) and deletes
/// log files older than the retention period. Logging failures are swallowed: the app must keep running.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    public const string FilePrefix = "dotdeck-";
    public const string FileExtension = ".log";

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _logsDir;
    private readonly TimeProvider _time;
    private readonly int _retentionDays;
    private readonly Lock _gate = new();

    public FileAppLog(string logsDir, TimeProvider time, int retentionDays = 14)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        _logsDir = logsDir;
        _time = time;
        _retentionDays = retentionDays;
    }

    public event EventHandler<AppLogEntry>? EntryWritten;

    public void Write(AppLogLevel level, string message, Exception? exception = null)
    {
        AppLogEntry entry;
        try
        {
            entry = new AppLogEntry(
                _time.GetLocalNow(),
                level,
                SecretMasker.MaskText(Truncate(message)),
                exception is null ? null : SecretMasker.MaskText(Truncate(DescribeException(exception))));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Building the entry failed (clock, masking). Never record the raw message.
            return;
        }

        try
        {
            var line = Format(entry);
            lock (_gate)
            {
                Directory.CreateDirectory(_logsDir);
                File.AppendAllText(GetFilePath(entry.Timestamp), line, s_utf8NoBom);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let logging break the app (I/O, access, invalid path); subscribers still get the entry.
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A faulty subscriber must not break the caller.
        }
    }

    /// <summary>Deletes log files older than the retention period. Returns the number deleted.</summary>
    public int CleanupOldFiles()
    {
        var cutoff = _time.GetLocalNow().Date.AddDays(-_retentionDays);
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_logsDir))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(_logsDir, FilePrefix + "*" + FileExtension))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Skip files in use.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore enumeration failures.
        }

        return deleted;
    }

    public string GetFilePath(DateTimeOffset timestamp) =>
        Path.Combine(_logsDir, FilePrefix + timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileExtension);

    /// <summary>Upper bound for one message/exception text, keeping masking cost bounded.</summary>
    public const int MaxTextLength = 16 * 1024;

    private static string Truncate(string? text)
    {
        text ??= string.Empty;
        return text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength), " …[truncated]");
    }

    private static string DescribeException(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Overridden ToString() may throw; fall back to the type name only.
            return exception.GetType().FullName ?? "Exception";
        }
    }

    private static string Format(AppLogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(" [").Append(LevelTag(entry.Level)).Append("] ")
          .Append(entry.Message.ReplaceLineEndings(" | "))
          .AppendLine();
        if (entry.Exception is not null)
        {
            sb.AppendLine(entry.Exception);
        }

        return sb.ToString();
    }

    private static string LevelTag(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => "DBG",
        AppLogLevel.Info => "INF",
        AppLogLevel.Warning => "WRN",
        AppLogLevel.Error => "ERR",
        _ => "???",
    };
}
