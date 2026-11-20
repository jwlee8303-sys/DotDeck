using DotDeck.Core.Paths;

namespace DotDeck.Core.Icons;

/// <summary>
/// Finds the tool configuration folder an item is in, from its path only (no disk access, so it is cheap for
/// thousands of tree rows). The nearest ancestor named .claude, .codex, .gemini (agy) or .agents (shared) is the
/// root, so a tool folder nested in another belongs to its own tool; the built-in homes from <see cref="AppPaths"/>
/// are the fallback.
/// </summary>
public sealed class IconScopeResolver
{
    private static readonly (string Name, IconTool Tool)[] s_rootNames =
    [
        (".claude", IconTool.Claude),
        (".codex", IconTool.Codex),
        (".gemini", IconTool.Antigravity),
        (".agents", IconTool.Shared),
    ];

    private readonly (string Root, IconTool Tool)[] _homes;

    public IconScopeResolver(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var homes = new List<(string Root, IconTool Tool)>();
        foreach (var (home, tool) in new[] { (paths.ClaudeHome, IconTool.Claude), (paths.CodexHome, IconTool.Codex), (paths.AntigravityHome, IconTool.Antigravity) })
        {
            if (Normalize(home) is { } root)
            {
                homes.Add((root, tool));
            }
        }

        _homes = [.. homes];
    }

    /// <summary>
    /// Scope of the item at <paramref name="fullPath"/>, or null outside any tool folder. A tool folder itself has no
    /// scope (it gets its icon by name), even inside another tool's folder. Only fully qualified paths count; "." and
    /// ".." are resolved lexically, and the drive or UNC share is never a tool folder.
    /// </summary>
    public IconScope? ScopeOf(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || Normalize(fullPath) is not { } path)
        {
            return null;
        }

        var rest = path[(Path.GetPathRoot(path) ?? string.Empty).Length..].TrimStart('\\');
        if (rest.Length == 0)
        {
            return null;
        }

        var segments = rest.Split('\\');
        if (s_rootNames.Any(r => string.Equals(segments[^1], r.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        for (var i = segments.Length - 2; i >= 0; i--)
        {
            foreach (var (name, tool) in s_rootNames)
            {
                if (string.Equals(segments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return new IconScope(tool, string.Join('/', segments[(i + 1)..]));
                }
            }
        }

        // Homes not named like a tool folder (none today, but AppPaths owns the locations).
        foreach (var (root, tool) in _homes)
        {
            if (path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\')
            {
                return new IconScope(tool, path[(root.Length + 1)..].Replace('\\', '/'));
            }
        }

        return null;
    }

    /// <summary>Lexically normalized absolute path without a trailing separator; null for relative or invalid paths.</summary>
    private static string? Normalize(string path)
    {
        // A drive-relative or relative path depends on a current directory: no scope rather than a guess.
        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            // String-only for qualified paths: resolves ".", ".." and "/" without touching the disk.
            var full = Path.GetFullPath(path);
            return full.Length > (Path.GetPathRoot(full) ?? string.Empty).Length ? full.TrimEnd('\\') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
