using System.Globalization;
using System.Text;

namespace DotDeck.Core.AiCli.Sessions;

/// <summary>Only explicitly reviewed/closed sessions are eligible. Never follows reparse points.</summary>
public static class SessionRetention
{
    public const string ClosedFile = "closed";
    public const string LeaseFile = "session.lease";
    public const int RecentLimit = 20;

    public static void MarkClosed(string directory, DateTimeOffset now)
    {
        try
        {
            var marker = Path.Combine(directory, ClosedFile);
            if (!File.Exists(marker)) File.WriteAllText(marker, now.ToString("O", CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<string> Prune(string sessionsDirectory, DateTimeOffset now, IReadOnlySet<string>? activeIds = null)
    {
        var deleted = new List<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsDirectory));
        if (!Directory.Exists(root) || HasReparseAncestor(root)) return deleted;
        var eligible = new List<(string Directory, DateTimeOffset Closed)>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || activeIds?.Contains(Path.GetFileName(directory)) == true) continue;
            try
            {
                if (!SafeTree(directory)) continue;
                var marker = Path.Combine(directory, ClosedFile);
                if (File.Exists(marker) && new FileInfo(marker).Length < 128
                    && DateTimeOffset.TryParse(File.ReadAllText(marker), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var closed))
                    eligible.Add((directory, closed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var ordered = eligible.OrderByDescending(e => e.Closed).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            if (i < RecentLimit && now - item.Closed <= TimeSpan.FromDays(7)) continue;
            try
            {
                // The lease also protects sessions belonging to another running DotDeck instance.
                using (new FileStream(Path.Combine(item.Directory, LeaseFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var target = Path.GetFullPath(item.Directory);
                    if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) || !SafeTree(target)) continue;
                    // Delete children while holding the lease; the session ID is never reused by a start.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(target))
                    {
                        if (Path.GetFileName(entry) == LeaseFile) continue;
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                }
                File.Delete(Path.Combine(item.Directory, LeaseFile));
                Directory.Delete(item.Directory);
                deleted.Add(item.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }

    private static bool HasReparseAncestor(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static bool SafeTree(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0 && !SafeTree(entry)) return false;
        }
        return true;
    }
}
