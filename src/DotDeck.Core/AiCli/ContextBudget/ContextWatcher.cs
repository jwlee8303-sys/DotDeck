using DotDeck.Core.Files;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// Watches the folders of a <see cref="ContextCollection"/> for changes to instruction, skill and configuration files
/// (name and content). Recursive targets (skill, rule and project config folders) react to <c>*.md</c>, <c>*.json</c>
/// and <c>*.toml</c>; plain folders (CLI homes, the project's ancestors) only to <c>*.md</c>, <c>*.toml</c> and the
/// known MCP/settings file names, because CLIs rewrite other JSON files there (caches, status) all the time.
/// The callback is debounced and runs on any thread. Folders that cannot be watched are skipped.
/// </summary>
public sealed class ContextWatcher : IDisposable
{
    private static readonly string[] s_recursiveExtensions = [".md", ".json", ".toml"];
    private static readonly string[] s_plainExtensions = [".md", ".toml"];
    private static readonly string[] s_plainNames = ["settings.json", ".mcp.json", "mcp_config.json", ".claude.json"];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Debouncer _debouncer;

    public ContextWatcher(IEnumerable<ContextWatchTarget> targets, Action changed)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(changed);
        _debouncer = new Debouncer(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3), changed);
        foreach (var target in targets)
        {
            try
            {
                var watcher = new FileSystemWatcher(target.Directory)
                {
                    IncludeSubdirectories = target.Recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024,
                };
                var recursive = target.Recursive;
                FileSystemEventHandler onEvent = (_, e) =>
                {
                    if (Matters(e.Name, recursive))
                    {
                        _debouncer.Trigger();
                    }
                };
                watcher.Changed += onEvent;
                watcher.Created += onEvent;
                watcher.Deleted += onEvent;
                watcher.Renamed += (_, e) =>
                {
                    if (Matters(e.Name, recursive) || Matters(e.OldName, recursive))
                    {
                        _debouncer.Trigger();
                    }
                };
                watcher.Error += (_, _) => _debouncer.Trigger();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
            }
        }
    }

    /// <summary>Whether a change to <paramref name="name"/> (relative to the watched folder) can change the analysis.</summary>
    internal static bool Matters(string? name, bool recursive)
    {
        var fileName = Path.GetFileName(name ?? string.Empty);
        var extension = Path.GetExtension(fileName);
        if (recursive)
        {
            // Folder events (a skill folder added or removed) have no extension and count too.
            return extension.Length == 0 || s_recursiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        return s_plainExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || s_plainNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _debouncer.Dispose();
    }
}
