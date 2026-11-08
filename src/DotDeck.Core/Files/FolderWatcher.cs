namespace DotDeck.Core.Files;

/// <summary>Watches a folder tree; the callback is debounced and may run on any thread.</summary>
public interface IFolderWatcherFactory
{
    /// <summary>Starts watching, or returns null when the folder cannot be watched (missing, no access).</summary>
    IDisposable? Watch(string directory, Action changed);
}

/// <summary>
/// <see cref="FileSystemWatcher"/>-based watcher (SPEC 2.3: live tree, debounced). Only names matter to the tree,
/// so content writes are ignored. A buffer overflow (Error) also triggers, because the consumer rescans anyway.
/// </summary>
public sealed class FolderWatcherFactory : IFolderWatcherFactory
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(2);

    public IDisposable? Watch(string directory, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        try
        {
            return new FolderWatcher(directory, changed);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Watches the files directly inside a folder for content and name changes (open documents: external change
    /// detection). Non-recursive. Same debounce as the tree watcher.
    /// </summary>
    public static IDisposable? WatchContent(string directory, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        try
        {
            return new FolderWatcher(directory, changed, contents: true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private sealed class FolderWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Debouncer _debouncer;

        public FolderWatcher(string directory, Action changed, bool contents = false)
        {
            _debouncer = new Debouncer(DefaultDelay, DefaultMaxDelay, changed);
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = !contents,
                NotifyFilter = contents
                    ? NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    : NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            if (contents)
            {
                _watcher.Changed += OnEvent;
            }

            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => _debouncer.Trigger();
            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _debouncer.Dispose();
        }

        private void OnEvent(object sender, FileSystemEventArgs e) => _debouncer.Trigger();
    }
}
