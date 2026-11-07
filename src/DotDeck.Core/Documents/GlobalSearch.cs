using System.Text;
using System.Text.RegularExpressions;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;

namespace DotDeck.Core.Documents;

/// <summary>A folder to search: project name, root folder and its excluded folders.</summary>
public sealed record SearchRoot(string ProjectName, string Root, ExcludedFolderPolicy Excluded);

/// <summary>One matching line. <see cref="Line"/> and <see cref="Column"/> are 1-based.</summary>
public sealed record SearchHit(string ProjectName, string Path, string RelativePath, int Line, int Column, string LineText);

/// <summary>
/// Searches text files of all projects (SPEC Phase 4 global search). Secret, binary, large and linked files
/// and excluded folders are skipped; results are capped.
/// </summary>
public static class GlobalSearch
{
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxHits = 2000;

    private const int MaxLineChars = 300;

    public static IEnumerable<SearchHit> Run(IFileStore store, IReadOnlyList<SearchRoot> roots, Regex regex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(regex);
        var count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!store.DirectoryExists(root.Root))
            {
                continue;
            }

            foreach (var file in Files(store, root, root.Root, string.Empty, cancellationToken))
            {
                if (!seen.Add(file.FullPath) || ReadText(store, file.FullPath) is not { } text)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root.Root, file.FullPath);
                var lineNumber = 0;
                foreach (var line in text.Split('\n'))
                {
                    lineNumber++;
                    cancellationToken.ThrowIfCancellationRequested();
                    Match match;
                    try
                    {
                        match = regex.Match(line.TrimEnd('\r'));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (!match.Success)
                    {
                        continue;
                    }

                    var display = line.TrimEnd('\r').Trim();
                    yield return new SearchHit(root.ProjectName, file.FullPath, relative, lineNumber, match.Index + 1,
                        display.Length > MaxLineChars ? display[..MaxLineChars] + "…" : display);
                    if (++count >= MaxHits)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static IEnumerable<FileSystemEntry> Files(IFileStore store, SearchRoot root, string directory, string relative, CancellationToken ct)
    {
        IReadOnlyList<FileSystemEntry> children;
        try
        {
            children = store.GetChildren(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in children.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint)
            {
                continue;
            }

            var childRelative = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
            if (entry.IsDirectory)
            {
                if (root.Excluded.IsExcluded(childRelative))
                {
                    continue;
                }

                foreach (var file in Files(store, root, entry.FullPath, childRelative, ct))
                {
                    yield return file;
                }
            }
            else if (!SecretFilePolicy.IsSecret(entry.Name) && entry.Length <= MaxFileBytes)
            {
                yield return entry;
            }
        }
    }

    // UTF-8 (with or without BOM) or UTF-16 with BOM; files with NUL bytes are treated as binary.
    private static string? ReadText(IFileStore store, string path)
    {
        byte[] bytes;
        try
        {
            bytes = store.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }
}
