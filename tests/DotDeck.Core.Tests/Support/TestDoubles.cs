using DotDeck.Core.Logging;
using DotDeck.Core.Platform;

namespace DotDeck.Core.Tests.Support;

/// <summary>In-memory process environment. Never reads the real environment.</summary>
public sealed class FakeProcessEnvironment : IProcessEnvironment
{
    public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new()
    {
        [Environment.SpecialFolder.UserProfile] = @"C:\Users\tester",
        [Environment.SpecialFolder.ApplicationData] = @"C:\Users\tester\AppData\Roaming",
        [Environment.SpecialFolder.LocalApplicationData] = @"C:\Users\tester\AppData\Local",
    };

    public string CurrentDirectory { get; set; } = @"C:\work";

    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);

    public string GetFolderPath(Environment.SpecialFolder folder) => Folders.GetValueOrDefault(folder, string.Empty);
}

/// <summary>Manually advanced clock. Local time zone is UTC for deterministic dates.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>Log that keeps entries in memory for assertions.</summary>
public sealed class ListAppLog : IAppLog
{
    public List<AppLogEntry> Entries { get; } = [];

    public event EventHandler<AppLogEntry>? EntryWritten;

    public void Write(AppLogLevel level, string message, Exception? exception = null)
    {
        var entry = new AppLogEntry(DateTimeOffset.UnixEpoch, level, message, exception?.ToString());
        Entries.Add(entry);
        EntryWritten?.Invoke(this, entry);
    }
}

/// <summary>Unique temporary directory deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DotDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
