using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;

namespace DotDeck.Core.AiCli.Snapshots;

public enum ChangeKind
{
    Modified,
    Created,
    Deleted,
}

/// <summary>One file the session changed.</summary>
public sealed record ChangeItem
{
    public required string Path { get; init; }

    public required ChangeKind Kind { get; init; }

    public long? OldLength { get; init; }

    public long? NewLength { get; init; }

    /// <summary>Where the previous content is kept (null when it was never captured).</summary>
    public BackupSet? PreviousSet { get; init; }

    public BackupFile? PreviousFile { get; init; }

    /// <summary>Hash of the file when the summary was made (null for deleted or uncapturable files).</summary>
    public string? CurrentSha256 { get; init; }

    public DateTime? CurrentWriteTimeUtc { get; init; }

    public bool IsSecret => SecretFilePolicy.IsSecret(System.IO.Path.GetFileName(Path));

    /// <summary>
    /// A "created" file found while the start index was cut short: it may have existed before the session, so it is
    /// reported but never sent to the recycle bin.
    /// </summary>
    public bool PossiblyPreexisting { get; init; }

    /// <summary>
    /// A deleted file the start index did not list (excluded folder, CLI system record or cut-short index): its kept
    /// content is from a write during the session, and it may not have existed at the start, so it is never recreated.
    /// </summary>
    public bool StartStateUnknown { get; init; }

    /// <summary>Created files go to the recycle bin; others need their previous content.</summary>
    public string? RevertBlockedReason => Kind switch
    {
        ChangeKind.Created when PossiblyPreexisting => "시작 시 색인이 잘려 새 파일인지 확인할 수 없습니다. 직접 확인하세요.",
        ChangeKind.Deleted when StartStateUnknown => "세션 시작 때 있던 파일인지 확인할 수 없어 다시 만들지 않습니다.",
        ChangeKind.Created => null,
        _ when PreviousFile is null => IsSecret ? "비밀 파일은 이전 내용을 보관하지 않습니다." : "이전 내용이 기록되지 않았습니다(큰 파일·바이너리 또는 캡처 범위 밖).",
        _ => null,
    };
}

public sealed record ChangeSummary(IReadOnlyList<ChangeItem> Items, bool IndexTruncated);

/// <summary>Compares the start index with the scope now and attaches the earliest captured content per file.</summary>
public static class ChangeSummaryBuilder
{
    public static ChangeSummary Build(SessionSnapshot start, ScopeIndex now, BackupStore sessionBackups, IFileStore store, SnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(sessionBackups);

        var previous = new Dictionary<string, (BackupSet Set, BackupFile File)>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sessionBackups.List().OrderBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var file in set.Files)
            {
                previous.TryAdd(file.OriginalPath, (set, file));
            }
        }

        var items = new List<ChangeItem>();
        foreach (var (path, before) in start.Index.Files)
        {
            if (!now.Files.TryGetValue(path, out var after))
            {
                // A file missing from a cut-short index may simply not have been reached.
                if (!now.Truncated || !store.FileExists(path))
                {
                    items.Add(Item(path, ChangeKind.Deleted, before, null, previous, store, limits));
                }

                continue;
            }

            // Same size and time usually means untouched, but a captured file is cheap to verify by content
            // (a tool may keep the time stamp, or delete and recreate the file).
            var sameMetadata = before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc;
            if (sameMetadata && !previous.ContainsKey(path))
            {
                continue;
            }

            var item = Item(path, ChangeKind.Modified, before, after, previous, store, limits);
            if (item.PreviousFile is { } kept && item.CurrentSha256 is { } sha && SameContent(sessionBackups, item.PreviousSet!, kept, sha))
            {
                continue; // Rewritten with identical content (only the time changed).
            }

            if (sameMetadata && item.CurrentSha256 is null)
            {
                continue; // Captured but no longer hashable (e.g. now unreadable): metadata says unchanged.
            }

            items.Add(item);
        }

        foreach (var (path, after) in now.Files)
        {
            // With a complete start index, a path missing from it did not exist then, even if a later write captured
            // it (a file created and then written again in the session): it stays "created". Only when the index was
            // cut short does a capture show the file may be older; it is then reported below as modified.
            if (!start.Index.Files.ContainsKey(path) && !(start.Index.Truncated && previous.ContainsKey(path)))
            {
                items.Add(Item(path, ChangeKind.Created, null, after, previous, store, limits) with { PossiblyPreexisting = start.Index.Truncated });
            }
        }

        // Files captured before a write but missing from the start index (cut short, or in an excluded folder). A
        // complete index would have listed an indexable path that existed then: such a capture is of a file the
        // session created (and may have deleted again), never something to bring back.
        foreach (var (path, kept) in previous)
        {
            if (!start.Index.Files.ContainsKey(path)
                && (start.Index.Truncated || !SessionSnapshots.IsInScope(path, start.Roots))
                && items.All(i => !string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                var exists = now.Files.TryGetValue(path, out var after) || store.FileExists(path);
                var item = Item(path, exists ? ChangeKind.Modified : ChangeKind.Deleted, null, after, previous, store, limits) with
                {
                    OldLength = kept.File.Length,
                    StartStateUnknown = !exists,
                };
                if (!(exists && item.CurrentSha256 is { } sha && SameContent(sessionBackups, kept.Set, kept.File, sha)))
                {
                    items.Add(item);
                }
            }
        }

        // System records are absent from both indexes. A tool write is user work even without a content backup.
        foreach (var write in SessionSnapshots.ReadToolWrites(store, sessionBackups))
        {
            if (previous.ContainsKey(write.Path) || SessionSnapshots.IsInScope(write.Path, start.Roots)
                || items.Any(i => string.Equals(i.Path, write.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DotDeck.Core.Files.FileSystemEntry? entry;
            try
            {
                entry = store.GetEntry(write.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                items.Add(Item(write.Path, ChangeKind.Modified, null, null, previous, store, limits));
                continue;
            }
            if (entry is null && !write.Existed)
            {
                continue;
            }

            var after = entry is { IsDirectory: false } ? new IndexEntry(write.Path, entry.Length, entry.LastWriteTimeUtc) : null;
            items.Add(Item(write.Path, after is null ? ChangeKind.Deleted : ChangeKind.Modified, null, after, previous, store, limits) with
            {
                StartStateUnknown = after is null,
            });
        }

        return new ChangeSummary([.. items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)], start.Index.Truncated || now.Truncated);
    }

    private static ChangeItem Item(
        string path, ChangeKind kind, IndexEntry? before, IndexEntry? after,
        Dictionary<string, (BackupSet Set, BackupFile File)> previous, IFileStore store, SnapshotLimits limits)
    {
        string? sha = null;
        DateTime? time = after?.LastWriteTimeUtc;
        if (kind != ChangeKind.Deleted)
        {
            sha = TryHash(store, path, limits, out var entryTime);
            time = entryTime ?? time;
        }

        var kept = kind == ChangeKind.Created ? default : previous.GetValueOrDefault(path);
        return new ChangeItem
        {
            Path = path,
            Kind = kind,
            OldLength = before?.Length,
            NewLength = after?.Length,
            PreviousSet = kept.Set,
            PreviousFile = kept.File,
            CurrentSha256 = sha,
            CurrentWriteTimeUtc = time,
        };
    }

    internal static string? TryHash(IFileStore store, string path, SnapshotLimits limits, out DateTime? writeTime)
    {
        writeTime = null;
        try
        {
            var entry = store.GetEntry(path);
            if (entry is null || entry.IsDirectory)
            {
                return null;
            }

            writeTime = entry.LastWriteTimeUtc;
            return entry.Length > limits.MaxContentBytes ? null : TextFileReader.ComputeHash(store.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameContent(BackupStore backups, BackupSet set, BackupFile file, string currentSha)
    {
        try
        {
            return string.Equals(TextFileReader.ComputeHash(backups.ReadContent(set, file)), currentSha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public enum RevertStatus
{
    Reverted,

    /// <summary>The file changed again after the summary was made; ask before reverting with <c>force</c>.</summary>
    ChangedSinceSummary,

    NotRevertible,
    Failed,
}

public sealed record RevertResult(ChangeItem Item, RevertStatus Status, string? Message = null, BackupSet? BeforeRestore = null, RecycleTicket? Recycled = null);

/// <summary>
/// Reverts single changes: modified and deleted files get their previous content back through
/// <see cref="SafeWriter.Restore"/> (which backs up the current file to the user's backups first), created files go
/// to the recycle bin. A file edited again after the summary is never overwritten without <c>force</c>. Targets are
/// re-checked against the session roots right before acting: a target or parent folder that became a link (junction,
/// symlink) since the summary is refused, so a revert can never write outside the scope.
/// </summary>
public sealed class RevertPlanner(IFileStore store, SafeWriter writer, IRecycleBin recycleBin, SnapshotLimits limits, IReadOnlyList<SnapshotRoot> roots)
{
    public RevertResult Revert(ChangeItem item, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RevertBlockedReason is { } blocked)
        {
            return new(item, RevertStatus.NotRevertible, blocked);
        }

        if (UnsafeTargetReason(item.Path) is { } unsafeReason)
        {
            return new(item, RevertStatus.NotRevertible, unsafeReason);
        }

        if (!force && ChangedSinceSummary(item))
        {
            return new(item, RevertStatus.ChangedSinceSummary, "요약 이후 파일이 다시 바뀌었습니다.");
        }

        try
        {
            if (item.Kind == ChangeKind.Created)
            {
                if (!store.FileExists(item.Path))
                {
                    return new(item, RevertStatus.Reverted, "이미 없습니다.");
                }

                if (!recycleBin.CanRecycle(item.Path))
                {
                    return new(item, RevertStatus.NotRevertible, "이 위치에는 휴지통이 없어 영구 삭제가 됩니다. 직접 삭제하세요.");
                }

                return new(item, RevertStatus.Reverted, Recycled: recycleBin.Recycle(item.Path));
            }

            // Without force the write itself is conditional on the state the summary showed, closing the gap between
            // the check above and the replace (a deleted file must still be missing, others must hash the same).
            var expectation = force || (item.Kind != ChangeKind.Deleted && item.CurrentSha256 is null)
                ? null
                : new RestoreExpectation(item.Kind == ChangeKind.Deleted ? null : item.CurrentSha256);
            var restored = writer.Restore(item.PreviousSet!, item.PreviousFile!, expectation);
            return new(item, RevertStatus.Reverted, BeforeRestore: restored.Backup);
        }
        catch (ExternalChangeException)
        {
            return new(item, RevertStatus.ChangedSinceSummary, "요약 이후 파일이 다시 바뀌었습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(item, RevertStatus.Failed, ex.Message);
        }
    }

    /// <summary>Null when <paramref name="path"/> lies inside a root with no link on the way; otherwise why not.</summary>
    private string? UnsafeTargetReason(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "경로를 확인할 수 없습니다.";
        }

        var root = roots
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)))
            .Where(r => full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
        if (root is null)
        {
            return "세션 범위 밖의 파일입니다.";
        }

        try
        {
            // The file itself and every folder between it and the root (the root was resolved when the session began).
            for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
            {
                if (store.GetEntry(current) is { IsReparsePoint: true })
                {
                    return "경로에 링크(정션·심볼릭 링크)가 있어 되돌리지 않습니다.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "경로를 확인할 수 없습니다.";
        }

        return null;
    }

    private bool ChangedSinceSummary(ChangeItem item)
    {
        if (item.Kind == ChangeKind.Deleted)
        {
            return store.FileExists(item.Path);
        }

        var sha = ChangeSummaryBuilder.TryHash(store, item.Path, limits, out var time);
        return item.CurrentSha256 is not null
            ? !string.Equals(sha, item.CurrentSha256, StringComparison.OrdinalIgnoreCase)
            : time != item.CurrentWriteTimeUtc;
    }
}
