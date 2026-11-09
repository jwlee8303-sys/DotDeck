using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotDeck.Core.AiCli.Snapshots;

/// <summary>What was recorded when a session started.</summary>
public sealed record SessionSnapshot(ScopeIndex Index, bool FullCapture, int CapturedFiles)
{
    /// <summary>The indexed roots with their excluded folders.</summary>
    public IReadOnlyList<SnapshotRoot> Roots { get; init; } = [];
}

/// <summary>
/// Captures previous contents so AI changes can be reviewed and reverted (DESIGN "스냅샷·변경 요약"). Contents go to the
/// session's own backup store (<c>&lt;session&gt;\backups</c>, encrypted like every backup), never to the user's backup
/// history, so a long session cannot push the user's own backups out. Secret files and binary or large files are
/// never captured; their changes are reported without a way back.
/// </summary>
public static class SessionSnapshots
{
    public const string BackupsFolder = "backups";
    public const string StartReason = "session-start";
    public const string PreWriteReason = "pre-write";

    /// <summary>The session backup store (retention is effectively unlimited; the folder is removed with the session).</summary>
    public static BackupStore OpenStore(IFileStore store, string sessionDirectory, TimeProvider time, IBackupProtector? protector) =>
        new(store, Path.Combine(sessionDirectory, BackupsFolder), time, retention: int.MaxValue, protector);

    /// <summary>
    /// Indexes the scope and captures previous contents: every text file when the scope is small (A15), otherwise only
    /// the open documents inside the scope. Call after all documents were saved.
    /// </summary>
    public static SessionSnapshot Capture(
        IReadOnlyList<SnapshotRoot> roots, IReadOnlyList<string> openDocuments, IFileStore store, BackupStore sessionBackups, SnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(openDocuments);
        ArgumentNullException.ThrowIfNull(sessionBackups);
        var index = ScopeIndex.Build(roots, store, limits);

        var candidates = index.Files.Values.Where(f => f.Length <= limits.MaxContentBytes && !SecretFilePolicy.IsSecret(Path.GetFileName(f.Path))).ToList();
        var full = !index.Truncated && candidates.Count <= limits.FullCaptureMaxFiles && candidates.Sum(f => f.Length) <= limits.FullCaptureMaxBytes;
        // Open documents are always captured, even when a cut-short index never reached them.
        var paths = full
            ? candidates.Select(f => f.Path)
            : openDocuments.Where(p => IsInScope(p, roots));

        var sources = new List<BackupSource>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadCapturable(store, path, limits) is { } bytes)
            {
                sources.Add(new BackupSource(path, bytes));
            }
        }

        if (sources.Count > 0)
        {
            sessionBackups.Create(sources, StartReason);
        }

        return new SessionSnapshot(index, full, sources.Count) { Roots = [.. roots] };
    }

    /// <summary>
    /// Called by the guard just before Claude writes <paramref name="path"/>: keeps the original once per path (an
    /// earlier capture, at start or before a previous write, already holds the true original). Never throws: a
    /// capture problem only means the change cannot be reverted later.
    /// </summary>
    public static bool CapturePreWrite(IFileStore store, BackupStore sessionBackups, string path, SnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionBackups);
        try
        {
            // Look-up, read and create under one lock: two guards for the same path (parallel tool calls) must not
            // both capture, or the second could keep content written after the first write. The short wait keeps
            // the hook well inside its time limit; when it runs out the write goes ahead uncaptured.
            using var _ = store.AcquireLock(Path.Combine(sessionBackups.BackupsDirectory, CaptureLockFile), limits.PreWriteLockTimeout);
            // Keep tool provenance even when there is no original to capture (new, large or binary files).
            var writes = Path.Combine(sessionBackups.BackupsDirectory, ToolWritesFolder);
            var full = Path.GetFullPath(path);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
            var record = Path.Combine(writes, key + ".json");
            if (!store.FileExists(record))
            {
                store.CreateDirectory(writes);
                store.WriteAllTextAtomic(record, JsonSerializer.Serialize(new ToolWrite(full, store.FileExists(full))));
            }

            if (sessionBackups.ListFor(path).Count > 0 || ReadCapturable(store, path, limits) is not { } bytes)
            {
                return false;
            }

            sessionBackups.Create([new BackupSource(path, bytes)], PreWriteReason);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private const string CaptureLockFile = "capture.lock";
    private const string ToolWritesFolder = "tool-writes";

    internal sealed record ToolWrite(string Path, bool Existed);

    internal static IReadOnlyList<ToolWrite> ReadToolWrites(IFileStore store, BackupStore backups)
    {
        var result = new List<ToolWrite>();
        var folder = Path.Combine(backups.BackupsDirectory, ToolWritesFolder);
        try
        {
            if (!store.DirectoryExists(folder))
            {
                return result;
            }

            foreach (var entry in store.GetChildren(folder).Where(e => !e.IsDirectory && !e.IsReparsePoint && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ToolWrite>(store.ReadAllText(entry.FullPath)) is { Path.Length: > 0 } write)
                    {
                        result.Add(write);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A missing or damaged provenance record must not prevent review of other changes.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The existing index and content backups still provide the ordinary change summary.
        }

        return result;
    }

    /// <summary>
    /// Inside one of the roots and not under an excluded folder or a CLI system record (lexical; reading the file
    /// refuses links). A path that is not indexable this way but was captured before a tool write is still summarized.
    /// </summary>
    internal static bool IsInScope(string path, IReadOnlyList<SnapshotRoot> roots)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in roots)
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
            if (!full.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeFile = full[(rootPath.Length + 1)..];
            if (root.SystemRecords.IsSystemFile(relativeFile))
            {
                continue;
            }

            var relativeFolder = Path.GetDirectoryName(relativeFile) ?? string.Empty;
            var excluded = false;
            for (var folder = relativeFolder; folder.Length > 0; folder = Path.GetDirectoryName(folder) ?? string.Empty)
            {
                if (root.Excluded.IsExcluded(folder))
                {
                    excluded = true;
                    break;
                }
            }

            if (!excluded)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[]? ReadCapturable(IFileStore store, string path, SnapshotLimits limits)
    {
        if (SecretFilePolicy.IsSecret(Path.GetFileName(path)))
        {
            return null;
        }

        try
        {
            var entry = store.GetEntry(path);
            if (entry is null || entry.IsDirectory || entry.IsReparsePoint || entry.Length > limits.MaxContentBytes)
            {
                return null;
            }

            var bytes = store.ReadAllBytes(path);
            return TextFileReader.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8192))) ? null : bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
