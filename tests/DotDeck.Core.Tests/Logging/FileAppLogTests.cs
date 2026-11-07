using DotDeck.Core.Logging;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Logging;

public sealed class FileAppLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 10, 30, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Writes_to_daily_file_with_level_tag()
    {
        var logsDir = _temp.Combine("logs");
        var log = new FileAppLog(logsDir, _time);

        log.Info("hello");
        log.Error("boom", new InvalidOperationException("bad"));

        var file = Path.Combine(logsDir, "dotdeck-20261107.log");
        var text = File.ReadAllText(file);
        Assert.Contains("2026-11-07 10:30:00.000 [INF] hello", text, StringComparison.Ordinal);
        Assert.Contains("[ERR] boom", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: bad", text, StringComparison.Ordinal);
    }

    [Fact]
    public void File_has_no_bom()
    {
        var logsDir = _temp.Combine("logs");
        var log = new FileAppLog(logsDir, _time);

        log.Info("한글");

        var bytes = File.ReadAllBytes(log.GetFilePath(_time.GetLocalNow()));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void Masks_secrets_in_message_exception_and_event()
    {
        var logsDir = _temp.Combine("logs");
        var log = new FileAppLog(logsDir, _time);
        AppLogEntry? raised = null;
        log.EntryWritten += (_, e) => raised = e;

        log.Warning("env API_KEY=sk-live-123", new InvalidOperationException("token=abc999"));

        var text = File.ReadAllText(log.GetFilePath(_time.GetLocalNow()));
        Assert.DoesNotContain("sk-live-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc999", text, StringComparison.Ordinal);
        Assert.NotNull(raised);
        Assert.DoesNotContain("sk-live-123", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc999", raised.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiline_message_is_written_on_one_line()
    {
        var log = new FileAppLog(_temp.Combine("logs"), _time);

        log.Info("a\r\nb");

        var lines = File.ReadAllLines(log.GetFilePath(_time.GetLocalNow()));
        Assert.Single(lines);
        Assert.EndsWith("a | b", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_does_not_throw_when_directory_is_unusable()
    {
        // A file where the logs directory should be makes CreateDirectory fail.
        var blocker = _temp.Combine("blocked");
        File.WriteAllText(blocker, "x");
        var log = new FileAppLog(blocker, _time);
        var raised = false;
        log.EntryWritten += (_, _) => raised = true;

        log.Info("still works");

        Assert.True(raised);
    }

    [Fact]
    public void Invalid_path_characters_do_not_throw()
    {
        var log = new FileAppLog(_temp.Combine("bad\0dir"), _time);

        log.Info("ok");
    }

    [Fact]
    public void Exception_with_throwing_ToString_is_logged_by_type()
    {
        var log = new FileAppLog(_temp.Combine("logs"), _time);
        AppLogEntry? raised = null;
        log.EntryWritten += (_, e) => raised = e;

        log.Error("x", new ThrowingToStringException());

        Assert.NotNull(raised);
        Assert.Contains(nameof(ThrowingToStringException), raised.Exception, StringComparison.Ordinal);
    }

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("no");
    }

    [Fact]
    public void Faulty_subscriber_does_not_break_caller()
    {
        var log = new FileAppLog(_temp.Combine("logs"), _time);
        log.EntryWritten += (_, _) => throw new InvalidOperationException("subscriber");

        log.Info("ok");
    }

    [Fact]
    public void Cleanup_deletes_only_files_older_than_retention()
    {
        var logsDir = _temp.Combine("logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "dotdeck-20261023.log"), "old");   // 15 days
        File.WriteAllText(Path.Combine(logsDir, "dotdeck-20261024.log"), "edge");  // 14 days
        File.WriteAllText(Path.Combine(logsDir, "dotdeck-20261107.log"), "today");
        File.WriteAllText(Path.Combine(logsDir, "dotdeck-garbage.log"), "x");
        File.WriteAllText(Path.Combine(logsDir, "other.log"), "x");
        var log = new FileAppLog(logsDir, _time, retentionDays: 14);

        var deleted = log.CleanupOldFiles();

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(logsDir, "dotdeck-20261023.log")));
        Assert.True(File.Exists(Path.Combine(logsDir, "dotdeck-20261024.log")));
        Assert.True(File.Exists(Path.Combine(logsDir, "dotdeck-garbage.log")));
        Assert.True(File.Exists(Path.Combine(logsDir, "other.log")));
    }

    [Fact]
    public void Cleanup_on_missing_directory_returns_zero()
    {
        var log = new FileAppLog(_temp.Combine("missing"), _time);

        Assert.Equal(0, log.CleanupOldFiles());
    }
}
