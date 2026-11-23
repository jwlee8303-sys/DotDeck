using System.Text;

namespace DotDeck.Core.Usage;

/// <summary>
/// Parse results of log files, reused while a file's length and write time stay the same. Files that are no longer
/// asked for are dropped. Not thread-safe: one refresh at a time.
/// </summary>
internal sealed class LogFileCache<T>(Func<string, T> parse)
{
    private readonly Dictionary<string, (long Length, DateTime WriteTime, T Value)> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parse results of <paramref name="files"/>; a file that cannot be read is skipped.</summary>
    public List<T> Get(IEnumerable<FileInfo> files)
    {
        var results = new List<T>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            wanted.Add(file.FullName);
            if (_entries.TryGetValue(file.FullName, out var entry) && entry.Length == file.Length && entry.WriteTime == file.LastWriteTimeUtc)
            {
                results.Add(entry.Value);
                continue;
            }

            try
            {
                var value = parse(file.FullName);
                _entries[file.FullName] = (file.Length, file.LastWriteTimeUtc, value);
                results.Add(value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked meanwhile: counted again on the next refresh.
            }
        }

        foreach (var stale in _entries.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _entries.Remove(stale);
        }

        return results;
    }
}

/// <summary>Reading CLI log files that may be written meanwhile.</summary>
internal static class LogFiles
{
    /// <summary>The lines of a log the CLI may be appending to (shared read; a partial last line is the caller's to skip).</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Log files below <paramref name="root"/> matching <paramref name="pattern"/> written since <paramref name="sinceUtc"/>.</summary>
    public static IEnumerable<FileInfo> RecentFiles(string root, string pattern, DateTime sinceUtc) =>
        new DirectoryInfo(root)
            .EnumerateFiles(pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => f.LastWriteTimeUtc >= sinceUtc);
}
