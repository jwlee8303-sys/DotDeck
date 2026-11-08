using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

/// <summary>One file inside a backup set.</summary>
public sealed record BackupFile(string OriginalPath, string RelativePath, long Length, string Sha256);

/// <summary>A backup folder (<c>backups\&lt;yyyyMMdd-HHmmss&gt;[-n]</c>) and its manifest.</summary>
public sealed record BackupSet(string Id, string Directory, DateTimeOffset CreatedAt, string Reason, IReadOnlyList<BackupFile> Files);

/// <summary>Content to back up: the bytes currently on disk for <paramref name="OriginalPath"/>.</summary>
public sealed record BackupSource(string OriginalPath, byte[] Content);

/// <summary>
/// Stores pre-save copies under <c>backups\&lt;ts&gt;[-n]\&lt;drive&gt;\&lt;path&gt;</c> with a <c>manifest.json</c>,
/// lists them and prunes to the newest <see cref="Retention"/> sets. Secret files are never backed up.
/// </summary>
public sealed class BackupStore
{
    public const string ManifestFileName = "manifest.json";
    public const int DefaultRetention = 50;

    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private static readonly TimeSpan s_lockTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFileStore _store;
    private readonly TimeProvider _time;

    public BackupStore(IFileStore store, string backupsDirectory, TimeProvider time, int retention = DefaultRetention)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(backupsDirectory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        _store = store;
        _time = time;
        BackupsDirectory = backupsDirectory;
        Retention = retention;
    }

    /// <summary>Where new backup sets go (settings can move it; existing sets stay where they were).</summary>
    public string BackupsDirectory
    {
        get => _backupsDirectory;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            _backupsDirectory = value;
        }
    }

    private string _backupsDirectory = string.Empty;

    public int Retention { get; set; }

    /// <summary>Creates one backup set holding all <paramref name="sources"/>, then prunes old sets.</summary>
    public BackupSet Create(IReadOnlyList<BackupSource> sources, string reason)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (sources.Count == 0)
        {
            throw new ArgumentException("Nothing to back up.", nameof(sources));
        }

        BackupSet set;
        using (_store.AcquireLock(Path.Combine(BackupsDirectory, ".lock"), s_lockTimeout))
        {
            var now = _time.GetLocalNow();
            var id = NextFreeId(now.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            var directory = Path.Combine(BackupsDirectory, id);
            _store.CreateDirectory(directory);
            try
            {
                var files = new List<BackupFile>(sources.Count);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                {
                    var relative = MapToRelativePath(source.OriginalPath);
                    if (!used.Add(relative))
                    {
                        continue;
                    }

                    var target = Path.Combine(directory, relative);
                    _store.CreateDirectory(Path.GetDirectoryName(target)!);
                    _store.WriteAllBytes(target, source.Content);
                    files.Add(new BackupFile(
                        Path.GetFullPath(source.OriginalPath), relative, source.Content.Length, TextFileReader.ComputeHash(source.Content)));
                }

                set = new BackupSet(id, directory, now, reason, files);
                var manifest = new Manifest(1, now, reason, files);
                _store.WriteAllTextAtomic(Path.Combine(directory, ManifestFileName), JsonSerializer.Serialize(manifest, s_json));
            }
            catch
            {
                TryDeleteDirectory(directory);
                throw;
            }
        }

        Prune(keep: set.Id);
        return set;
    }

    /// <summary>All readable backup sets, newest first. Folders without a valid manifest are skipped.</summary>
    public IReadOnlyList<BackupSet> List()
    {
        if (!_store.DirectoryExists(BackupsDirectory))
        {
            return [];
        }

        var result = new List<BackupSet>();
        foreach (var entry in _store.GetChildren(BackupsDirectory))
        {
            if (entry.IsDirectory && TryLoad(entry.FullPath, entry.Name) is { } set)
            {
                result.Add(set);
            }
        }

        result.Sort(static (a, b) => CompareIds(b.Id, a.Id));
        return result;
    }

    /// <summary>Backups that contain <paramref name="originalPath"/>, newest first.</summary>
    public IReadOnlyList<(BackupSet Set, BackupFile File)> ListFor(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        return [.. List().SelectMany(s => s.Files
            .Where(f => string.Equals(f.OriginalPath, full, StringComparison.OrdinalIgnoreCase))
            .Select(f => (s, f)))];
    }

    /// <summary>Reads a backed-up file. Throws <see cref="InvalidDataException"/> if the content no longer matches the manifest.</summary>
    public byte[] ReadContent(BackupSet set, BackupFile file)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(file);
        var bytes = _store.ReadAllBytes(Path.Combine(set.Directory, file.RelativePath));
        if (!string.Equals(TextFileReader.ComputeHash(bytes), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup content is damaged: {file.RelativePath}");
        }

        return bytes;
    }

    /// <summary>
    /// Maps an absolute path to its place inside a backup set: <c>C:\a\b</c> → <c>C\a\b</c>,
    /// <c>\\server\share\a</c> → <c>UNC\server\share\a</c>.
    /// </summary>
    public static string MapToRelativePath(string originalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        var full = Path.GetFullPath(originalPath);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var rest = full[root.Length..].TrimStart('\\', '/');
        string prefix;
        if (root.Length >= 2 && root[1] == ':')
        {
            prefix = root[..1].ToUpperInvariant();
        }
        else if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            prefix = Path.Combine(["UNC", .. root.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
        }
        else
        {
            prefix = "ROOT";
        }

        return string.IsNullOrEmpty(rest) ? prefix : Path.Combine(prefix, rest);
    }

    private string NextFreeId(string baseId)
    {
        var id = baseId;
        for (var n = 2; _store.DirectoryExists(Path.Combine(BackupsDirectory, id)) || _store.FileExists(Path.Combine(BackupsDirectory, id)); n++)
        {
            id = $"{baseId}-{n.ToString(CultureInfo.InvariantCulture)}";
        }

        return id;
    }

    // Sets younger than this may belong to a save still in progress (another window or instance) and are kept.
    private static readonly TimeSpan s_pruneGrace = TimeSpan.FromMinutes(1);

    private void Prune(string keep)
    {
        var sets = List();
        var cutoff = _time.GetUtcNow() - s_pruneGrace;
        foreach (var old in sets.Skip(Retention))
        {
            if (!string.Equals(old.Id, keep, StringComparison.OrdinalIgnoreCase) && old.CreatedAt < cutoff)
            {
                TryDeleteDirectory(old.Directory);
            }
        }
    }

    private BackupSet? TryLoad(string directory, string id)
    {
        try
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!_store.FileExists(manifestPath))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<Manifest>(_store.ReadAllText(manifestPath), s_json);
            if (manifest?.Files is null || manifest.Reason is null)
            {
                return null;
            }

            // Reject manifest entries that point outside the set folder.
            var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (manifest.Files.Any(f => f.RelativePath is null || f.OriginalPath is null
                || !Path.GetFullPath(Path.Combine(directory, f.RelativePath)).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new BackupSet(id, directory, manifest.CreatedAt, manifest.Reason, manifest.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    // "20261107-101010-10" must sort after "...-9": compare the timestamp, then the numeric suffix.
    private static int CompareIds(string a, string b)
    {
        static (string Stamp, int N) Split(string id)
        {
            var dash = id.Length > TimestampFormat.Length ? id.LastIndexOf('-') : -1;
            return dash > 0 && int.TryParse(id.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? (id[..dash], n)
                : (id, 1);
        }

        var (sa, na) = Split(a);
        var (sb, nb) = Split(b);
        var c = string.CompareOrdinal(sa, sb);
        return c != 0 ? c : na.CompareTo(nb);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (_store.DirectoryExists(directory))
            {
                _store.DeleteDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Manifest(int SchemaVersion, DateTimeOffset CreatedAt, string? Reason, IReadOnlyList<BackupFile>? Files);
}
