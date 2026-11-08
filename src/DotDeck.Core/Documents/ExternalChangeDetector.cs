using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

public enum DiskState
{
    Unchanged,
    Modified,
    Deleted,
}

/// <summary>Result of comparing an open document's baseline with the disk. <see cref="Sha256"/> is null when deleted.</summary>
public sealed record DiskCheck(DiskState State, string? Sha256);

/// <summary>
/// Decides whether a file changed since <see cref="TextFileSnapshot"/> was taken. Size and mtime equal to the
/// snapshot are treated as unchanged without reading; otherwise the content hash decides (a touch is not a change,
/// and DotDeck's own writes match the snapshot it just recorded).
/// </summary>
public static class ExternalChangeDetector
{
    /// <param name="force">Hash even when size and mtime match (after a watcher event: tools may preserve mtime).</param>
    public static DiskCheck Check(IFileStore store, TextFileSnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        var entry = store.GetEntry(snapshot.Path);
        if (entry is null || entry.IsDirectory)
        {
            return new DiskCheck(DiskState.Deleted, null);
        }

        if (!force && entry.Length == snapshot.Length && entry.LastWriteTimeUtc == snapshot.LastWriteTimeUtc)
        {
            return new DiskCheck(DiskState.Unchanged, snapshot.Sha256);
        }

        byte[] bytes;
        try
        {
            bytes = store.ReadAllBytes(snapshot.Path);
        }
        catch (FileNotFoundException)
        {
            return new DiskCheck(DiskState.Deleted, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new DiskCheck(DiskState.Deleted, null);
        }

        var hash = TextFileReader.ComputeHash(bytes);
        var same = string.Equals(hash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase);
        return new DiskCheck(same ? DiskState.Unchanged : DiskState.Modified, hash);
    }
}
