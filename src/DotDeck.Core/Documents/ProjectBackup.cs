using System.IO.Compression;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

public sealed record ProjectBackupResult(string ZipPath, int FileCount, int SkippedSecrets);

/// <summary>
/// Copies whole projects into one zip at a user-chosen folder (settings menu). Secret files, linked items and
/// excluded folders are left out, so the archive never holds key material.
/// </summary>
public static class ProjectBackup
{
    public static ProjectBackupResult CreateZip(IFileStore store, IReadOnlyList<SearchRoot> projects, string destinationDirectory, TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(time);
        if (!Path.IsPathFullyQualified(destinationDirectory))
        {
            throw new ArgumentException("백업 위치는 전체 경로여야 합니다.", nameof(destinationDirectory));
        }

        store.CreateDirectory(destinationDirectory);
        var stamp = $"DotDeck-projects-{time.GetLocalNow():yyyyMMdd-HHmmss}";
        var zipPath = Path.Combine(destinationDirectory, stamp + ".zip");
        for (var n = 2; store.FileExists(zipPath); n++)
        {
            zipPath = Path.Combine(destinationDirectory, $"{stamp} ({n}).zip");
        }

        var created = false;
        var files = 0;
        var secrets = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
            created = true;
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var project in projects)
            {
                if (!store.DirectoryExists(project.Root))
                {
                    continue;
                }

                var folder = UniqueName(SafeName(project.ProjectName), usedNames);
                foreach (var (entry, relative) in Walk(store, project, project.Root, string.Empty, cancellationToken))
                {
                    if (string.Equals(entry.FullPath, zipPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // the archive itself, when the destination is inside a project
                    }

                    if (SecretFilePolicy.IsSecret(entry.Name))
                    {
                        secrets++;
                        continue;
                    }

                    var bytes = store.ReadAllBytes(entry.FullPath);
                    var item = zip.CreateEntry(folder + "/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                    item.LastWriteTime = entry.LastWriteTimeUtc.ToLocalTime();
                    using var stream = item.Open();
                    stream.Write(bytes);
                    files++;
                }
            }
        }
        catch
        {
            if (created)
            {
                TryDelete(zipPath);
            }

            throw;
        }

        return new ProjectBackupResult(zipPath, files, secrets);
    }

    private static IEnumerable<(FileSystemEntry Entry, string Relative)> Walk(IFileStore store, SearchRoot root, string directory, string relative, CancellationToken ct)
    {
        foreach (var entry in store.GetChildren(directory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint)
            {
                continue;
            }

            var childRelative = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
            if (!entry.IsDirectory)
            {
                yield return (entry, childRelative);
            }
            else if (!root.Excluded.IsExcluded(childRelative))
            {
                foreach (var file in Walk(store, root, entry.FullPath, childRelative, ct))
                {
                    yield return file;
                }
            }
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return safe.Length == 0 ? "project" : safe;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{name} ({i})";
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
