using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

/// <summary>
/// Folders hidden from a project's tree. An entry is either a folder name, matched at any depth
/// ("node_modules"), or a path relative to the project root starting with '/' or '\' ("/projects").
/// Matching is case-insensitive.
/// </summary>
public sealed class ExcludedFolderPolicy
{
    /// <summary>SPEC 2.3 defaults for every project.</summary>
    public static IReadOnlyList<string> CommonDefaults { get; } =
        ["node_modules", "bin", "obj", ".git", ".vs", ".venv", "dist", "__pycache__"];

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootedPaths = new(StringComparer.OrdinalIgnoreCase);

    public ExcludedFolderPolicy(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            // A separator inside ("src/gen") can only mean a path, so it is rooted too. A trailing one
            // ("debug/") just marks a folder, as in .gitignore, and stays a name.
            var relative = NormalizeRelative(entry);
            if (relative.Length == 0)
            {
                continue;
            }

            if (entry[0] is '/' or '\\' || relative.Contains('\\', StringComparison.Ordinal))
            {
                _rootedPaths.Add(relative);
            }
            else
            {
                _names.Add(relative);
            }
        }
    }

    public static ExcludedFolderPolicy None { get; } = new([]);

    /// <summary>Projects no longer exclude folders implicitly.</summary>
    public static IReadOnlyList<string> DefaultsFor(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [];
    }

    public static ExcludedFolderPolicy For(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // Keep the legacy persisted field readable, but never hide project content because of it.
        return None;
    }

    /// <param name="relativePath">Folder path relative to the project root, e.g. "src\node_modules".</param>
    public bool IsExcluded(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            return false;
        }

        var name = normalized[(normalized.LastIndexOf('\\') + 1)..];
        return _names.Contains(name) || _rootedPaths.Contains(normalized);
    }

    private static string NormalizeRelative(string path) => path.Replace('/', '\\').Trim('\\');
}
