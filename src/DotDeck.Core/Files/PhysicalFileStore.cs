namespace DotDeck.Core.Files;

/// <summary><see cref="IFileStore"/> backed by the real file system (System.IO).</summary>
public sealed class PhysicalFileStore : IFileStore
{
    public static PhysicalFileStore Instance { get; } = new();

    // Access errors are never swallowed: callers must be able to tell "empty" from "not allowed" (listing)
    // and must not silently skip content (copying). Non-recursive, so children are never opened here.
    private static readonly EnumerationOptions s_childrenOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FileSystemEntry? GetEntry(string path)
    {
        if (Directory.Exists(path))
        {
            return ToEntry(new DirectoryInfo(path));
        }

        return File.Exists(path) ? ToEntry(new FileInfo(path)) : null;
    }

    public IReadOnlyList<FileSystemEntry> GetChildren(string directory)
    {
        var dir = new DirectoryInfo(directory);
        return dir.EnumerateFileSystemInfos("*", s_childrenOptions).Select(ToEntry).ToList();
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string source, string destination, bool overwrite = false)
    {
        if (Directory.Exists(source))
        {
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                // Case-only rename of the same directory is allowed.
                if (!IsSamePathIgnoringCase(source, destination))
                {
                    throw new IOException($"Destination already exists: {destination}");
                }
            }

            Directory.Move(source, destination);
            return;
        }

        if (!overwrite && Directory.Exists(destination))
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        File.Move(source, destination, overwrite);
    }

    public void Copy(string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        if (Directory.Exists(source))
        {
            CopyDirectory(new DirectoryInfo(source), destination);
        }
        else
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public IDisposable AcquireLock(string lockPath, TimeSpan timeout)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static void CopyDirectory(DirectoryInfo source, string destination)
    {
        var sourceFull = Path.TrimEndingDirectorySeparator(source.FullName) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(destination).StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Cannot copy a folder into itself.");
        }

        // Snapshot the whole source tree before creating anything. If the destination is reachable from
        // the source through a link the string check above cannot see, newly created items are still never
        // enumerated, so the copy cannot recurse into itself.
        var plan = new List<(FileSystemInfo Info, string Target)>();
        CollectTree(source, destination, plan);

        Directory.CreateDirectory(destination);
        foreach (var (info, target) in plan)
        {
            if (info is DirectoryInfo)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                File.Copy(info.FullName, target, overwrite: false);
            }
        }
    }

    private static void CollectTree(DirectoryInfo source, string destination, List<(FileSystemInfo, string)> plan)
    {
        foreach (var info in source.EnumerateFileSystemInfos("*", s_childrenOptions))
        {
            var target = Path.Combine(destination, info.Name);
            if (info is DirectoryInfo childDir)
            {
                // Do not follow junctions/symlinks: copying through them can escape the tree or loop.
                if (childDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                plan.Add((childDir, target));
                CollectTree(childDir, target, plan);
            }
            else
            {
                plan.Add((info, target));
            }
        }
    }

    private static bool IsSamePathIgnoringCase(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static FileSystemEntry ToEntry(FileSystemInfo info) => new(
        info.Name,
        info.FullName,
        info is DirectoryInfo,
        info is FileInfo f ? f.Length : 0,
        info.LastWriteTimeUtc,
        info.Attributes);
}
