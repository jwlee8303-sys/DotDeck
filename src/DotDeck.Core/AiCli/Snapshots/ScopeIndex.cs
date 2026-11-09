using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.AiCli.Snapshots;

/// <summary>Snapshot limits (A15). Defaults follow the approved design; the settings dialog may expose them.</summary>
public sealed record SnapshotLimits
{
    /// <summary>Full capture at start only when the scope has at most this many text files…</summary>
    public int FullCaptureMaxFiles { get; init; } = 2_000;

    /// <summary>…totalling at most this many bytes.</summary>
    public long FullCaptureMaxBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Indexing stops here (the summary then says the index was cut short).</summary>
    public int MaxIndexedFiles { get; init; } = 50_000;

    /// <summary>Files larger than this are compared by size and time only and never captured.</summary>
    public long MaxContentBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>How long a pre-write capture waits for another capture (the PreToolUse hook has 10 seconds).</summary>
    public TimeSpan PreWriteLockTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>One root of the indexed scope with its project's excluded folders.</summary>
public sealed record SnapshotRoot(string Path, ExcludedFolderPolicy Excluded)
{
    /// <summary>CLI system records under this root (when it is a CLI config folder): never indexed or captured.</summary>
    public SystemRecordPolicy SystemRecords { get; init; } = SystemRecordPolicy.None;
}

/// <summary>One indexed file.</summary>
public sealed record IndexEntry(string Path, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// The files of a scope at one moment (size and time; no contents). Excluded folders are skipped and links are not
/// followed, like the explorer. Paths are compared case-insensitively. <see cref="Truncated"/> means the index is
/// incomplete: the file limit was reached or a folder could not be read.
/// </summary>
public sealed record ScopeIndex(IReadOnlyDictionary<string, IndexEntry> Files, bool Truncated)
{
    public static ScopeIndex Build(IReadOnlyList<SnapshotRoot> roots, IFileStore store, SnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(limits);

        var files = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var incomplete = false;
        foreach (var root in roots)
        {
            var pending = new Stack<(string Directory, string Relative)>();
            pending.Push((root.Path, string.Empty));
            while (pending.Count > 0 && !truncated)
            {
                var (directory, relative) = pending.Pop();
                IReadOnlyList<FileSystemEntry> children;
                try
                {
                    children = store.GetChildren(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not tracked, and the index is incomplete: a file found there later may be older than the session.
                    incomplete = true;
                    continue;
                }

                foreach (var child in children)
                {
                    var childRelative = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
                    if (child.IsDirectory)
                    {
                        if (!child.IsReparsePoint && !root.Excluded.IsExcluded(childRelative) && !root.SystemRecords.IsSystemFolder(childRelative))
                        {
                            pending.Push((child.FullPath, childRelative));
                        }

                        continue;
                    }

                    if (root.SystemRecords.IsSystemFile(childRelative))
                    {
                        continue;
                    }

                    if (files.Count >= limits.MaxIndexedFiles)
                    {
                        truncated = true;
                        break;
                    }

                    files.TryAdd(child.FullPath, new IndexEntry(child.FullPath, child.Length, child.LastWriteTimeUtc));
                }
            }
        }

        return new ScopeIndex(files, truncated || incomplete);
    }
}
