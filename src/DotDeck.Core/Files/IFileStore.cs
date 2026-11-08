namespace DotDeck.Core.Files;

/// <summary>A move, copy or create was refused because something already exists at the destination (nothing was changed).</summary>
public sealed class DestinationExistsException(string path)
    : IOException($"Destination already exists: {path}")
{
    public string Path { get; } = path;
}

/// <summary>A directory copy failed after it created <see cref="Destination"/>; the partial copy belongs to the caller.</summary>
public sealed class PartialCopyException(string destination, Exception innerException)
    : IOException($"Copy failed partway: {destination}\n{innerException.Message}", innerException)
{
    public string Destination { get; } = destination;
}

/// <summary>Snapshot of one file system entry.</summary>
public sealed record FileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes)
{
    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    public bool IsReadOnly => Attributes.HasFlag(FileAttributes.ReadOnly);

    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>
/// File system access used by Core. Implementations: <see cref="PhysicalFileStore"/> (real disk).
/// Paths are absolute. Methods throw the usual <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>.
/// </summary>
public interface IFileStore
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Returns null when the path does not exist.</summary>
    FileSystemEntry? GetEntry(string path);

    /// <summary>Immediate children of a directory (no recursion).</summary>
    IReadOnlyList<FileSystemEntry> GetChildren(string directory);

    byte[] ReadAllBytes(string path);

    /// <summary>Final target of a symbolic link or junction, or null when <paramref name="path"/> is not a link or does not exist.</summary>
    string? ResolveLinkTarget(string path);

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes from the start of a file.</summary>
    byte[] ReadHead(string path, int maxBytes);

    void WriteAllBytes(string path, byte[] bytes);

    void CreateDirectory(string path);

    /// <summary>Creates an empty file. Throws <see cref="IOException"/> if a file or folder already exists there.</summary>
    void CreateFile(string path);

    /// <summary>Moves a file or directory. Never overwrites an existing destination unless <paramref name="overwrite"/> (files only).</summary>
    void Move(string source, string destination, bool overwrite = false);

    /// <summary>
    /// Copies a file, or a directory recursively. Never overwrites: an existing destination throws
    /// <see cref="DestinationExistsException"/> before anything is created. A failed file copy leaves no file;
    /// a directory copy that fails after creating the destination throws <see cref="PartialCopyException"/>
    /// (any other failure created nothing).
    /// </summary>
    void Copy(string source, string destination);

    /// <summary>
    /// Atomically replaces an existing <paramref name="destination"/> with <paramref name="source"/> (same volume),
    /// keeping the destination's attributes and ACL. <paramref name="source"/> no longer exists afterwards.
    /// </summary>
    void ReplaceFile(string source, string destination);

    /// <summary>Permanently deletes a directory tree. Only for app-owned folders (e.g. pruned backups).</summary>
    void DeleteDirectory(string path);

    /// <summary>Permanently deletes a file. Only for app-owned files (temp files, app data). User files go to the recycle bin.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Takes a cross-process exclusive lock represented by <paramref name="lockPath"/> (created if missing).
    /// Throws <see cref="IOException"/> if it cannot be acquired within <paramref name="timeout"/>.
    /// </summary>
    IDisposable AcquireLock(string lockPath, TimeSpan timeout);
}
