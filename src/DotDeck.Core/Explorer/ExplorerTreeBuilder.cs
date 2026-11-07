namespace DotDeck.Core.Explorer;

/// <summary>One visible row of a project's flattened tree.</summary>
/// <param name="Error">Why the folder's children could not be listed (the folder is then shown collapsed).</param>
public sealed record TreeRow(NodeInfo Node, int Depth, bool IsExpanded, string? Error = null);

/// <param name="RootError">Why the root could not be listed; <see cref="Rows"/> is then empty.</param>
/// <param name="StaleExpandedKeys">
/// Expanded keys whose folder no longer exists under a parent that was listed; callers drop them so the
/// saved state does not grow forever.
/// </param>
public sealed record TreeBuildResult(
    ProjectRootState RootState,
    IReadOnlyList<TreeRow> Rows,
    string? RootError,
    IReadOnlySet<string> StaleExpandedKeys);

/// <summary>
/// Builds the visible rows of a project from its source and the set of expanded folder keys.
/// Performs file system I/O: call it off the UI thread.
/// </summary>
public static class ExplorerTreeBuilder
{
    /// <summary>Keys compare case-insensitively, like Windows paths.</summary>
    public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Persistent key of a node: its path relative to the project root, or the virtual id.</summary>
    public static string KeyOf(NodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.RelativePath ?? node.Id;
    }

    public static TreeBuildResult Build(IProjectSource source, IReadOnlySet<string> expanded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expanded);
        var empty = new HashSet<string>(KeyComparer);
        var state = source.GetRootState();
        if (state == ProjectRootState.Missing)
        {
            return new TreeBuildResult(state, [], null, empty);
        }

        IReadOnlyList<NodeInfo> children;
        try
        {
            children = source.GetChildren(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TreeBuildResult(state, [], ex.Message, empty);
        }

        var rows = new List<TreeRow>();
        var stale = new HashSet<string>(KeyComparer);
        AddLevel(source, children, string.Empty, 0, expanded, rows, stale, cancellationToken);
        AddStaleDescendants(stale, expanded);
        return new TreeBuildResult(state, rows, null, stale);
    }

    /// <summary>
    /// Rows below <paramref name="parent"/> when it is expanded (its own row excluded), restoring nested expansion.
    /// Throws <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> if the parent cannot be listed.
    /// </summary>
    public static IReadOnlyList<TreeRow> BuildChildren(
        IProjectSource source,
        NodeInfo parent,
        int parentDepth,
        IReadOnlySet<string> expanded,
        ISet<string>? staleKeys = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expanded);
        var children = source.GetChildren(parent);
        var rows = new List<TreeRow>();
        var stale = staleKeys ?? new HashSet<string>(KeyComparer);
        AddLevel(source, children, KeyOf(parent), parentDepth + 1, expanded, rows, stale, cancellationToken);
        AddStaleDescendants(stale, expanded);
        return rows;
    }

    /// <summary>A deleted folder takes its expanded descendants with it ("gone" → also "gone\child").</summary>
    private static void AddStaleDescendants(ISet<string> stale, IReadOnlySet<string> expanded)
    {
        if (stale.Count == 0)
        {
            return;
        }

        var prefixes = stale.Select(s => s + Path.DirectorySeparatorChar).ToList();
        foreach (var key in expanded)
        {
            if (prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                stale.Add(key);
            }
        }
    }

    private static void AddLevel(
        IProjectSource source,
        IReadOnlyList<NodeInfo> children,
        string parentKey,
        int depth,
        IReadOnlySet<string> expanded,
        List<TreeRow> rows,
        ISet<string> stale,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CollectStale(children, parentKey, expanded, stale);
        foreach (var child in children)
        {
            if (!child.CanExpand || !expanded.Contains(KeyOf(child)))
            {
                rows.Add(new TreeRow(child, depth, false));
                continue;
            }

            IReadOnlyList<NodeInfo> grandChildren;
            try
            {
                grandChildren = source.GetChildren(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rows.Add(new TreeRow(child, depth, false, ex.Message));
                continue;
            }

            rows.Add(new TreeRow(child, depth, true));
            AddLevel(source, grandChildren, KeyOf(child), depth + 1, expanded, rows, stale, cancellationToken);
        }
    }

    private static void CollectStale(IReadOnlyList<NodeInfo> children, string parentKey, IReadOnlySet<string> expanded, ISet<string> stale)
    {
        HashSet<string>? present = null;
        foreach (var key in expanded)
        {
            if (!KeyComparer.Equals(ParentKeyOf(key), parentKey))
            {
                continue;
            }

            present ??= new HashSet<string>(children.Where(c => c.CanExpand).Select(KeyOf), KeyComparer);
            if (!present.Contains(key))
            {
                stale.Add(key);
            }
        }
    }

    internal static string ParentKeyOf(string key)
    {
        var separator = key.LastIndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? string.Empty : key[..separator];
    }
}
