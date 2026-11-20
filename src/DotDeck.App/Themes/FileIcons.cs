using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Markup;
using System.Windows.Media;
using DotDeck.Core.Files;
using DotDeck.Core.Icons;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;

namespace DotDeck.App.Themes;

/// <summary>
/// File and folder icons of the explorer tree and document tabs (docs/design/file-icons.md). Icons are embedded
/// <c>FileIcons/icons/&lt;key&gt;.xaml</c> DrawingImages, loaded on first use, frozen and cached. Bindings depend on
/// <see cref="Version"/> (icons changed: theme, mappings) and <see cref="DirtyVersion"/> (unsaved open files) so
/// only realized rows re-evaluate. UI icons (menus, toolbars) stay Segoe Fluent Icons.
/// </summary>
public static class FileIcons
{
    private const string IconPrefix = "FileIcons/icons/";
    private const string MaterialResource = "FileIcons/material-icons.json";
    private const string DotDeckResource = "FileIcons/dotdeck-icons.json";

    /// <summary>A hand-written mapping is a few KB; anything this large is not one.</summary>
    private const int MaxUserFileBytes = 1024 * 1024;

    private const int MaxRetries = 5;

    private static readonly Assembly s_assembly = typeof(FileIcons).Assembly;
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> s_images = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_dirty = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlySet<string>> s_available = new(() =>
        s_assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(IconPrefix, StringComparison.Ordinal) && n.EndsWith(".xaml", StringComparison.Ordinal))
            .Select(n => n[IconPrefix.Length..^".xaml".Length])
            .ToHashSet(StringComparer.Ordinal));

    private static readonly Lazy<string?> s_material = new(() => ReadResource(MaterialResource));
    private static readonly Lazy<string?> s_dotDeck = new(() => ReadResource(DotDeckResource));

    private static FileIconResolver? s_resolver;
    private static IconScopeResolver? s_scopes;
    private static AppPaths? s_paths;
    private static string? s_userJson;
    private static IDisposable? s_watcher;
    private static bool s_retryPending;
    private static int s_retries;
    private static IAppLog? s_log;
    private static int s_version;
    private static int s_dirtyVersion;
    private static bool s_initialized;
    private static IconTheme s_theme = IconTheme.Dark;

    /// <summary>WPF binds static properties through this event (<c>{Binding (themes:FileIcons.Version)}</c>).</summary>
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// <summary>Changes when icons may differ for the same item (theme switch, mapping reload).</summary>
    public static int Version => s_version;

    /// <summary>Changes when the set of unsaved open files changes.</summary>
    public static int DirtyVersion => s_dirtyVersion;

    /// <summary>Icon keys the app can draw (Material plus DotDeck's own).</summary>
    public static IReadOnlySet<string> AvailableKeys => s_available.Value;

    /// <summary>
    /// Called once at startup: tool scopes and the user's associations.json from <paramref name="paths"/>, warnings to
    /// <paramref name="log"/>. The icons follow theme switches and changes to associations.json.
    /// </summary>
    public static void Initialize(AppPaths paths, IAppLog log)
    {
        Configure(paths, log);
        s_theme = CurrentTheme();
        Bump();
        WatchUserFile();
        if (!s_initialized)
        {
            s_initialized = true;
            ThemeManager.Changed += (_, _) =>
            {
                s_theme = CurrentTheme();
                Bump();
            };
        }
    }

    /// <summary>Paths and log without watching or theme tracking (tests).</summary>
    internal static void Configure(AppPaths paths, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        s_paths = paths;
        s_log = log;
        s_scopes = new IconScopeResolver(paths);
        s_userJson = null;
        s_resolver = CreateResolver(ReadUserFile().Text);
    }

    /// <summary>
    /// Re-reads associations.json; when it changed, a new immutable resolver replaces the old one and bound icons
    /// refresh. Returns whether anything changed.
    /// </summary>
    public static bool Reload()
    {
        var file = ReadUserFile();
        if (!file.IsRead)
        {
            ScheduleRetry(); // locked mid-save: keep the current mapping and try again shortly
            return false;
        }

        s_retries = 0;
        if (s_resolver is not null && file.Text == s_userJson)
        {
            return false;
        }

        s_resolver = CreateResolver(file.Text);
        Bump();
        return true;
    }

    /// <summary>Icon key of a file or folder; <paramref name="fullPath"/> decides tool-specific icons.</summary>
    public static string KeyFor(string name, string? fullPath, bool isFolder, bool isExpanded) =>
        Resolver.Resolve(name, isFolder, isExpanded, s_theme, s_scopes?.ScopeOf(fullPath));

    public static ImageSource? ImageFor(string name, string? fullPath, bool isFolder, bool isExpanded) =>
        ImageOrDefault(KeyFor(name, fullPath, isFolder, isExpanded), isFolder, isExpanded);

    /// <summary>The image of <paramref name="key"/>, or the generic file/folder icon when that one cannot be drawn.</summary>
    internal static ImageSource? ImageOrDefault(string key, bool isFolder, bool isExpanded) =>
        Image(key) ?? Image(isFolder ? (isExpanded ? IconManifest.DefaultFolderExpanded : IconManifest.DefaultFolder) : IconManifest.DefaultFile);

    /// <summary>The frozen 16×16 image of <paramref name="key"/>, or null when it is missing or cannot be loaded (loaded and logged once).</summary>
    public static ImageSource? Image(string key) =>
        s_images.GetOrAdd(key, k => new Lazy<ImageSource?>(() => Load(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Records whether an open file has unsaved changes (tree badge).</summary>
    public static void SetDirty(string path, bool isDirty)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (NormalizePath(path) is not { } key)
        {
            return;
        }

        var changed = isDirty ? s_dirty.Add(key) : s_dirty.Remove(key);
        if (changed)
        {
            s_dirtyVersion++;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(DirtyVersion)));
        }
    }

    public static bool IsDirty(string? path) => path is not null && NormalizePath(path) is { } key && s_dirty.Contains(key);

    /// <summary>
    /// One spelling per file, so buffers (full paths) and tree rows (registered paths may hold "..", "/" or a
    /// device prefix) agree. Null for paths that are not absolute.
    /// </summary>
    internal static string? NormalizePath(string path)
    {
        try
        {
            var full = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
                : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
                : path;
            return Path.IsPathFullyQualified(full) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(full)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static FileIconResolver Resolver => s_resolver ??= CreateResolver();

    private static IconTheme CurrentTheme() => ThemeManager.Current == ThemeKind.Light ? IconTheme.Light : IconTheme.Dark;

    private static void Bump()
    {
        s_version++;
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Version)));
    }

    private static FileIconResolver CreateResolver(string? userJson = null)
    {
        s_userJson = userJson;
        return FileIconResolver.Create(
            new IconSources(s_material.Value, s_dotDeck.Value, userJson, AvailableKeys, s_paths?.IconAssociationsFile ?? "associations.json"),
            s_log ?? NullLog.Instance);
    }

    private static string? ReadResource(string name)
    {
        using var stream = s_assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Result of reading associations.json: <see cref="IsRead"/> is false when it exists but could not be read now.</summary>
    private readonly record struct UserFile(bool IsRead, string? Text);

    /// <summary>
    /// The user's associations.json (null text: no file, or too large). A locked or unreadable file is reported as not
    /// read, so the caller keeps the mapping it has instead of dropping the user's icons.
    /// </summary>
    private static UserFile ReadUserFile()
    {
        if (s_paths?.IconAssociationsFile is not { } path)
        {
            return new UserFile(true, null);
        }

        try
        {
            if (!File.Exists(path))
            {
                return new UserFile(true, null);
            }

            // At most the cap (plus one char to notice more), whatever the file grows to while it is read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxUserFileBytes + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > MaxUserFileBytes)
            {
                s_log?.Warning($"파일 아이콘({path})이 너무 커서(1MB 초과) 무시합니다.");
                return new UserFile(true, null);
            }

            return new UserFile(true, new string(buffer, 0, length));
        }
        catch (FileNotFoundException)
        {
            return new UserFile(true, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new UserFile(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (s_retries >= MaxRetries)
            {
                s_log?.Warning($"파일 아이콘({path})을 읽지 못해 이전 매핑을 유지합니다: {ex.Message}");
            }

            return new UserFile(false, null);
        }
    }

    /// <summary>Tries a locked file again a few times (an editor may hold it while saving).</summary>
    private static void ScheduleRetry()
    {
        if (s_retryPending || s_retries >= MaxRetries || System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        s_retryPending = true;
        s_retries++;
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background, (sender, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
            s_retryPending = false;
            ReloadAndLog();
        }, dispatcher);
        timer.Start();
    }

    /// <summary>
    /// Watches the folder of associations.json, or the app data folder (created at startup) until that folder exists.
    /// Every change re-installs the watcher, so a recreated folder or a watcher error never leaves a stale one.
    /// </summary>
    private static void WatchUserFile()
    {
        s_watcher?.Dispose();
        s_watcher = null;
        if (s_paths is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(s_paths.IconAssociationsFile)!;
        s_watcher = Directory.Exists(folder)
            ? FolderWatcherFactory.WatchContent(folder, OnUserFolderChanged)
            : new FolderWatcherFactory().Watch(s_paths.RoamingDataDir, OnUserFolderChanged);
    }

    /// <summary>Stops watching associations.json (app exit).</summary>
    public static void Shutdown()
    {
        s_paths = null;
        s_watcher?.Dispose();
        s_watcher = null;
    }

    private static void OnUserFolderChanged() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (s_paths is null)
            {
                return; // shut down meanwhile
            }

            WatchUserFile(); // the icons folder may just have been created, replaced or the watcher failed
            ReloadAndLog();
        });

    private static void ReloadAndLog()
    {
        if (s_paths is not null && Reload())
        {
            s_log?.Info("파일 아이콘 매핑(associations.json)을 다시 읽었습니다.");
        }
    }

    private static ImageSource? Load(string key)
    {
        try
        {
            using var stream = s_assembly.GetManifestResourceStream(IconPrefix + key + ".xaml");
            if (stream is null)
            {
                s_log?.Warning($"파일 아이콘 '{key}'이 앱에 없습니다.");
                return null;
            }

            var image = (DrawingImage)XamlReader.Load(stream);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is XamlParseException or InvalidCastException or InvalidOperationException or IOException)
        {
            s_log?.Warning($"파일 아이콘 '{key}'을 불러오지 못했습니다: {ex.Message}");
            return null;
        }
    }

    private sealed class NullLog : IAppLog
    {
        public static readonly NullLog Instance = new();

        public event EventHandler<AppLogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(AppLogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
