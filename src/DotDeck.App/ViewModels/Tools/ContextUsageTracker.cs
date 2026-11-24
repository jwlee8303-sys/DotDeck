using System.IO;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;

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
    private readonly IAppLog? _log;
    private readonly Dictionary<AiMonitorItemViewModel, Entry> _entries = [];
    private FileSystemWatcher? _statusLines;
    private bool _disposed;

    /// <param name="statusLineDir">Folder of the status line values (<see cref="StatusLineContextStore.Directory"/>), watched when it exists.</param>
    /// <param name="log">Debug lines whenever a row's match or state changes (which step matched, or why not).</param>
    public ContextUsageTracker(ContextUsageReader reader, Func<ContextBudgetSettings> loadSettings, string? statusLineDir, TimeProvider? time = null, IAppLog? log = null)
    {
        _log = log;
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

        /// <summary>Waiting for an older session of the same folder to be matched first (Claude, by folder).</summary>
        public bool Held { get; set; }

        public bool Finished { get; set; }

        public DateTimeOffset LastRead { get; set; }

        /// <summary>What was last logged (state, source, log, status line): logged again only when it changes.</summary>
        public string? Logged { get; set; }

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

        var toRead = new List<Entry>();
        foreach (var row in rows)
        {
            if (!_entries.TryGetValue(row, out var entry))
            {
                entry = new Entry(row);
                _entries[row] = entry;
                row.ContextTracked = true;
                if (row.IsRunning)
                {
                    entry.Debouncer = new Debouncer(Debounce, Debounce + Debounce, () => Post(() => Read(entry)));
                }
                else
                {
                    entry.Finished = true; // listed after it ended: read once, never watched
                }

                toRead.Add(entry);
            }
            else if (!row.IsRunning && !entry.Finished)
            {
                entry.Finished = true;
                entry.StopWatching();
                toRead.Add(entry); // the last answer may have been written within the debounce
            }
        }

        foreach (var entry in toRead)
        {
            Read(entry);
        }

        WatchStatusLines();
    }

    /// <summary>Retries sessions that have no matched log or status line yet (UI thread, every second from the monitor).</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values)
        {
            // Claude measured from its status line alone keeps looking for the transcript (the fallback when the
            // status line stops updating).
            if (!entry.Finished && !entry.Reading && now - entry.LastRead >= RetryInterval
                && (!entry.Matched || entry.Row.ContextUsage?.State != SessionLogState.Measured
                    || (entry.Row.Cli == CliKind.Claude && entry.LogPath is null)))
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
        var others = _entries.Values.Where(e => e != entry).ToList();
        var claimed = others.Where(e => e.Row.Cli == row.Cli)
            .SelectMany(e => new[] { e.StatusLineId, e.Row.Cli == CliKind.Claude ? e.Row.Session?.ConversationId : null })
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var claimedLogs = others.Select(e => e.LogPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = ContextBudgetViewModel.WorkingDirectory(row);
        var matchWindow = FolderMatchWindow(entry, folder);
        entry.Held = matchWindow.Held;

        var query = new ContextUsageQuery(
            new SessionLogQuery(row.Cli, entry.Held || (row.Cli == CliKind.Claude && entry.Finished) ? null : folder, row.StartedAt, row.Session?.ConversationId,
                ClaimedLogs: claimedLogs, FirstBefore: matchWindow.Before),
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

            var wasMatched = entry.Matched;
            if (task.IsCompletedSuccessfully)
            {
                var (result, settings) = task.Result;
                if (matchWindow != FolderMatchWindow(entry, folder))
                {
                    // The set of rivals changed while the read ran. Recompute before accepting a folder match.
                    entry.ReadAgain = true;
                }
                else if (!TakenByOther(entry, result))
                {
                    entry.LogPath = result.LogPath;
                    entry.StatusLineId = result.StatusLineId;
                    row.ShowContext(entry.Held ? Held(result.Usage) : result.Usage, settings);
                    row.ContextLogPath = entry.LogPath;
                    LogChange(entry, query, result);
                    if (!entry.Finished)
                    {
                        WatchLog(entry);
                    }
                }
            }

            if (entry.ReadAgain)
            {
                entry.ReadAgain = false;
                Read(entry);
            }

            if (!wasMatched && entry.Matched)
            {
                // A younger session of the same folder may have been waiting for this one.
                foreach (var held in _entries.Values.Where(e => e.Held && !e.Reading && !e.Finished).ToList())
                {
                    Read(held);
                }
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

    private (bool Held, DateTimeOffset? Before) FolderMatchWindow(Entry entry, string? folder)
    {
        var row = entry.Row;
        if (entry.Matched || entry.Finished || row.Cli != CliKind.Claude || row.Session?.ConversationId is not null || folder is null)
        {
            return default;
        }

        // First records after a younger rival started could belong to either terminal.
        var rivals = _entries.Values.Where(e => e != entry && !e.Matched && !e.Finished && e.Row.Cli == CliKind.Claude
                                                && e.Row.Session?.ConversationId is null
                                                && ContextBudgetViewModel.WorkingDirectory(e.Row) is { } other && SessionLogReader.SamePath(other, folder)).ToList();
        return (rivals.Any(e => e.Row.StartedAt <= row.StartedAt),
            rivals.Where(e => e.Row.StartedAt > row.StartedAt).Min(e => (DateTimeOffset?)e.Row.StartedAt));
    }

    private bool TakenByOther(Entry entry, ContextUsageResult result)
    {
        // A resumed DotDeck session is entitled to reuse the preceding row's exact transcript and status line.
        if (entry.Row.Cli != CliKind.Claude)
        {
            return result.StatusLineId is { } id && _entries.Values.Any(e => e != entry && e.Row.Cli == entry.Row.Cli && e.StatusLineId == id);
        }

        if (entry.Row.Session?.ConversationId is not null)
        {
            return false;
        }

        var logId = Path.GetFileNameWithoutExtension(result.LogPath);
        return _entries.Values.Any(e => e != entry && e.Row.Cli == CliKind.Claude
            && ((result.LogPath is not null && e.LogPath is not null && SessionLogReader.SamePath(e.LogPath, result.LogPath))
                || (result.StatusLineId is not null && result.StatusLineId == e.StatusLineId)
                || (logId is not null && string.Equals(logId, e.Row.Session?.ConversationId, StringComparison.OrdinalIgnoreCase))));
    }

    private static ContextUsage Held(ContextUsage usage) => usage.State == SessionLogState.Measured
        ? usage
        : usage with { Reason = "같은 폴더에서 먼저 시작한 Claude 세션이 매칭될 때까지 보류 (순서로만 구별 가능)" };

    private void LogChange(Entry entry, ContextUsageQuery query, ContextUsageResult result)
    {
        var usage = result.Usage;
        var summary = $"{usage.State}|{usage.Source}|{result.LogPath}|{result.StatusLineId}";
        if (_log is null || summary == entry.Logged)
        {
            return;
        }

        entry.Logged = summary;
        var reason = usage.Reason?.Replace('\n', ' ');
        _log.Debug($"컨텍스트 매칭: {entry.Row.Title} ({query.Log.Cli}, 시작 {query.Log.StartedAt.ToLocalTime():HH:mm:ss}, 폴더 {query.Log.WorkingDirectory ?? "(모름)"}, 세션 ID {query.Log.ConversationId ?? "-"}) → "
                   + $"{usage.State}/{usage.Source} \"{usage.Text}\" · 로그 {result.LogPath ?? "없음"} · 상태 표시줄 {result.StatusLineId ?? "없음"}"
                   + (reason is { Length: > 0 } ? " · " + reason : string.Empty));
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
