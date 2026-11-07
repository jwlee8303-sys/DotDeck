namespace DotDeck.Core.Files;

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

    void WriteAllBytes(string path, byte[] bytes);

    void CreateDirectory(string path);

    /// <summary>Moves a file or directory. Never overwrites an existing destination unless <paramref name="overwrite"/> (files only).</summary>
    void Move(string source, string destination, bool overwrite = false);

    /// <summary>Copies a file, or a directory recursively. Never overwrites an existing destination.</summary>
    void Copy(string source, string destination);

    /// <summary>Permanently deletes a file. Only for app-owned files (temp files, app data). User files go to the recycle bin.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Takes a cross-process exclusive lock represented by <paramref name="lockPath"/> (created if missing).
    /// Throws <see cref="IOException"/> if it cannot be acquired within <paramref name="timeout"/>.
    /// </summary>
    IDisposable AcquireLock(string lockPath, TimeSpan timeout);
}
