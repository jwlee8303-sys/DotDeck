using System.IO;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Files;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// Keeps the "컨텍스트" value of every AI 모니터 row up to date. A running session's matched log file and DotDeck's
/// status line folder are watched; a change re-reads the session after <see cref="Debounce"/>, from where the last
/// read stopped (<see cref="SessionLogReader"/>). A log that moves, disappears or shrinks is read from the start or
/// matched again. Sessions without a matched log are retried every <see cref="RetryInterval"/> (<see cref="Tick"/>).
/// A finished session is read once more, then no longer watched; its last value stays.
/// </summary>
public sealed class ContextUsageTracker : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    private readonly ContextUsageReader _reader;
    private readonly Func<ContextBudgetSettings> _loadSettings;
    private readonly string? _statusLineDir;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<AiMonitorItemViewModel, Entry> _entries = [];
    private FileSystemWatcher? _statusLines;
    private bool _disposed;

    /// <param name="statusLineDir">Folder of the status line values (<see cref="StatusLineContextStore.Directory"/>), watched when it exists.</param>
    public ContextUsageTracker(ContextUsageReader reader, Func<ContextBudgetSettings> loadSettings, string? statusLineDir, TimeProvider? time = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _loadSettings = loadSettings ?? throw new ArgumentNullException(nameof(loadSettings));
        _statusLineDir = statusLineDir;
        _time = time ?? TimeProvider.System;
        _ui = SynchronizationContext.Current;
    }

    private sealed class Entry(AiMonitorItemViewModel row) : IDisposable
    {
        public AiMonitorItemViewModel Row { get; } = row;

        public string? LogPath { get; set; }

        public string? StatusLineId { get; set; }

        public FileSystemWatcher? LogWatcher { get; set; }

        public string? WatchedPath { get; set; }

        public Debouncer? Debouncer { get; set; }

        public bool Reading { get; set; }

        public bool ReadAgain { get; set; }

        public bool Finished { get; set; }

        public DateTimeOffset LastRead { get; set; }

        public bool Matched => LogPath is not null || StatusLineId is not null;

        public void StopWatching()
        {
            LogWatcher?.Dispose();
            LogWatcher = null;
            WatchedPath = null;
            Debouncer?.Dispose();
            Debouncer = null;
        }

        public void Dispose() => StopWatching();
    }

    /// <summary>Starts following new rows and finishes rows whose session ended (UI thread).</summary>
    public void Track(IEnumerable<AiMonitorItemViewModel> rows)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (!_entries.TryGetValue(row, out var entry))
            {
                entry = new Entry(row);
                _entries[row] = entry;
                if (row.IsRunning)
                {
                    entry.Debouncer = new Debouncer(Debounce, Debounce + Debounce, () => Post(() => Read(entry)));
                }
                else
                {
                    entry.Finished = true; // listed after it ended: read once, never watched
                }

                Read(entry);
            }
            else if (!row.IsRunning && !entry.Finished)
            {
                entry.Finished = true;
                entry.StopWatching();
                Read(entry); // the last answer may have been written within the debounce
            }
        }

        WatchStatusLines();
    }

    /// <summary>Retries sessions that have no matched log or status line yet (UI thread, every second from the monitor).</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values)
        {
            if (!entry.Finished && !entry.Reading && now - entry.LastRead >= RetryInterval
                && (!entry.Matched || entry.Row.ContextUsage?.State != SessionLogState.Measured))
            {
                Read(entry);
            }
        }

        WatchStatusLines();
    }

    /// <summary>Stops following a cleared row.</summary>
    public void Forget(AiMonitorItemViewModel row)
    {
        if (_entries.Remove(row, out var entry))
        {
            entry.Dispose();
        }
    }

    private void Read(Entry entry)
    {
        if (_disposed || !_entries.ContainsKey(entry.Row))
        {
            return;
        }

        if (entry.Reading)
        {
            entry.ReadAgain = true;
            return;
        }

        entry.Reading = true;
        entry.LastRead = _time.GetUtcNow();
        var row = entry.Row;
        var claimed = _entries.Values.Where(e => e != entry && e.StatusLineId is not null).Select(e => e.StatusLineId!).ToHashSet(StringComparer.Ordinal);
        var query = new ContextUsageQuery(
            new SessionLogQuery(row.Cli, ContextBudgetViewModel.WorkingDirectory(row), row.StartedAt, row.Session?.ConversationId),
            entry.LogPath, entry.StatusLineId, claimed);
        var now = _time.GetUtcNow();
        _ = Task.Run(() =>
        {
            var settings = _loadSettings().Normalize();
            return (_reader.Read(query, settings, now), settings);
        }).ContinueWith(task => Post(() =>
        {
            entry.Reading = false;
            if (_disposed || !_entries.ContainsKey(row))
            {
                return;
            }

            if (task.IsCompletedSuccessfully)
            {
                var (result, settings) = task.Result;
                entry.LogPath = result.LogPath;
                entry.StatusLineId = result.StatusLineId ?? entry.StatusLineId;
                row.ShowContext(result.Usage, settings);
                if (!entry.Finished)
                {
                    WatchLog(entry);
                }
            }

            if (entry.ReadAgain)
            {
                entry.ReadAgain = false;
                Read(entry);
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Watches the entry's matched log (one file); a rename or delete of it re-reads, which matches again.</summary>
    private void WatchLog(Entry entry)
    {
        if (string.Equals(entry.WatchedPath, entry.LogPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        entry.LogWatcher?.Dispose();
        entry.LogWatcher = null;
        entry.WatchedPath = entry.LogPath;
        if (entry.LogPath is not { } path || Path.GetDirectoryName(path) is not { } dir)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 8 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => entry.Debouncer?.Trigger();
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => entry.Debouncer?.Trigger();
            watcher.Error += (_, _) => entry.Debouncer?.Trigger();
            watcher.EnableRaisingEvents = true;
            entry.LogWatcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not watchable: the retry in Tick keeps unmeasured sessions going.
        }
    }

    /// <summary>
    /// Watches DotDeck's status line folder (once it exists): a saved value re-reads the running Claude/agy rows that
    /// use it or have none yet.
    /// </summary>
    private void WatchStatusLines()
    {
        if (_statusLines is not null || _statusLineDir is null || !Directory.Exists(_statusLineDir))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_statusLineDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                InternalBufferSize = 8 * 1024,
            };
            RenamedEventHandler renamed = (_, e) => OnStatusLine(e.Name);
            FileSystemEventHandler changed = (_, e) => OnStatusLine(e.Name);
            watcher.Renamed += renamed; // the scripts write a temporary file and move it over the value
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.EnableRaisingEvents = true;
            _statusLines = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    private void OnStatusLine(string? name)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        var dash = stem.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return;
        }

        var cli = stem[..dash] switch
        {
            StatusLineContextStore.Claude => CliKind.Claude,
            StatusLineContextStore.Agy => (CliKind?)CliKind.Antigravity,
            _ => null,
        };
        var id = stem[(dash + 1)..];
        Post(() =>
        {
            foreach (var entry in _entries.Values)
            {
                if (!entry.Finished && entry.Row.Cli == cli && (entry.StatusLineId is null || entry.StatusLineId == id))
                {
                    entry.Debouncer?.Trigger();
                }
            }
        });
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _statusLines?.Dispose();
        _statusLines = null;
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }
}
