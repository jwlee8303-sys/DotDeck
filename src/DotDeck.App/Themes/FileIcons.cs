using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Markup;
using System.Windows.Media;
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

    private static readonly Assembly s_assembly = typeof(FileIcons).Assembly;
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> s_images = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_dirty = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlySet<string>> s_available = new(() =>
        s_assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(IconPrefix, StringComparison.Ordinal) && n.EndsWith(".xaml", StringComparison.Ordinal))
            .Select(n => n[IconPrefix.Length..^".xaml".Length])
            .ToHashSet(StringComparer.Ordinal));

    private static FileIconResolver? s_resolver;
    private static IconScopeResolver? s_scopes;
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

    /// <summary>Called once at startup: tool scopes from <paramref name="paths"/>, warnings to <paramref name="log"/>.</summary>
    public static void Initialize(AppPaths paths, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        s_log = log;
        s_scopes = new IconScopeResolver(paths);
        s_theme = CurrentTheme();
        Reload();
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

    /// <summary>Rebuilds the mapping (a new immutable resolver replaces the old one) and refreshes bound icons.</summary>
    public static void Reload()
    {
        s_resolver = CreateResolver();
        Bump();
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

    private static FileIconResolver CreateResolver()
    {
        string? material = null;
        using (var stream = s_assembly.GetManifestResourceStream(MaterialResource))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                material = reader.ReadToEnd();
            }
        }

        return FileIconResolver.Create(new IconSources(material, null, null, AvailableKeys), s_log ?? NullLog.Instance);
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
