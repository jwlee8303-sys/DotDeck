using DotDeck.Core.Files;
using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer.Visibility;

/// <summary>
/// Normal-mode tree of a folder project (SPEC 2.3): only what <see cref="VisibilityEvaluator"/> shows, laid out as a
/// tree, a compact tree (single-child folder chains merged into one row) or a flat file list, with pinned items
/// first. A folder is shown when something below it is shown or when it has its own Include rule.
/// <para>
/// Wraps the unfiltered source, so <see cref="ExplorerTreeBuilder"/> builds rows the same way in every mode. Finding
/// out whether a folder has visible content lists below it (stopping at the first hit; folders that cannot contain
/// anything visible are skipped). One instance serves one tree build: results are cached and not refreshed.
/// Performs I/O: use off the UI thread.
/// </para>
/// </summary>
public sealed class VisibleProjectSource : IProjectSource
{
    public const int DefaultEntryBudget = 200_000;

    private readonly IProjectSource _all;
    private readonly IFileStore _store;
    private readonly VisibilityEvaluator _evaluator;
    private readonly ProjectViewSettings _view;
    private readonly Dictionary<string, bool> _hasContent = new(RelativeKey.Comparer);
    private readonly Dictionary<string, IReadOnlyList<NodeInfo>> _children = new(RelativeKey.Comparer);

    /// <summary>Unfiltered listings, so finding content and building rows list each folder once.</summary>
    private readonly Dictionary<string, IReadOnlyList<NodeInfo>> _listed = new(RelativeKey.Comparer);
    private int _budget;

    /// <param name="all">Every item except excluded folders (<see cref="FolderProjectSource"/>).</param>
    /// <param name="entryBudget">Entries this instance may list; past it folders are assumed to have content and the flat list is cut.</param>
    public VisibleProjectSource(IProjectSource all, IFileStore store, VisibilityEvaluator evaluator, ProjectViewSettings view, int entryBudget = DefaultEntryBudget)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(view);
        if (all.RootPath is null)
        {
            throw new ArgumentException("Visibility applies to folder projects only.", nameof(all));
        }

        _all = all;
        _store = store;
        _evaluator = evaluator;
        _view = view;
        _budget = entryBudget;
    }

    public ProjectDefinition Project => _all.Project;

    public string? RootPath => _all.RootPath;

    /// <summary>The entry budget ran out: some folders were assumed to have content, or the flat list is incomplete.</summary>
    public bool IsTruncated { get; private set; }

    public ProjectRootState GetRootState() => _all.GetRootState();

    public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent)
    {
        if (parent is null)
        {
            var pinned = PinnedNodes();
            var rest = _view.Mode == ExplorerViewMode.Flat ? FlatFiles(null, skipPinned: true) : LayoutChildren(null, skipPinned: true);
            return [.. pinned, .. rest];
        }

        if (parent.Kind != NodeKind.Folder || parent.RelativePath is null)
        {
            return [];
        }

        // Pinned items are shown at the top only, at every depth (also inside a pinned folder).
        return _view.Mode == ExplorerViewMode.Flat ? FlatFiles(parent, skipPinned: true) : LayoutChildren(parent, skipPinned: true);
    }

    /// <summary>The visible children of a folder in tree form (no merging).</summary>
    public IReadOnlyList<NodeInfo> VisibleChildren(NodeInfo? folder)
    {
        var key = folder?.RelativePath ?? string.Empty;
        if (_children.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = new List<NodeInfo>();
        foreach (var child in List(folder))
        {
            var childKey = child.RelativePath!;
            if (child.Kind == NodeKind.File ? _evaluator.IsFileVisible(childKey) : IsFolderShown(child))
            {
                result.Add(child);
            }
        }

        _children[key] = result;
        return result;
    }

    public bool IsFolderShown(NodeInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var key = folder.RelativePath ?? string.Empty;
        return _evaluator.RuleOf(key) == VisibilityRule.Include || HasVisibleContent(folder);
    }

    /// <summary>Something below the folder is shown. Stops at the first visible item; links count as having content.</summary>
    public bool HasVisibleContent(NodeInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var key = folder.RelativePath ?? string.Empty;
        if (_hasContent.TryGetValue(key, out var known))
        {
            return known;
        }

        bool result;
        if (_evaluator.IsSubtreeDead(key))
        {
            result = false;
        }
        else if (folder.IsLink)
        {
            // Not followed (cycles); shown so that the user can still open it.
            result = true;
        }
        else
        {
            IReadOnlyList<NodeInfo> children;
            try
            {
                children = List(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Shown, so the error appears when the folder is expanded.
                children = [];
                _hasContent[key] = true;
                return true;
            }

            result = children.Any(c => c.Kind == NodeKind.File && _evaluator.IsFileVisible(c.RelativePath!))
                || children.Any(c => c.Kind == NodeKind.Folder && IsFolderShown(c));
        }

        _hasContent[key] = result;
        return result;
    }

    private IReadOnlyList<NodeInfo> LayoutChildren(NodeInfo? parent, bool skipPinned)
    {
        var children = VisibleChildren(parent);
        var result = new List<NodeInfo>(children.Count);
        foreach (var child in children)
        {
            var key = child.RelativePath!;
            if (skipPinned && _view.IsPinned(key))
            {
                continue;
            }

            result.Add(_view.Mode == ExplorerViewMode.Compact && child.Kind == NodeKind.Folder ? Compact(child) : child);
        }

        return result;
    }

    /// <summary>
    /// Merges a chain of folders that each show exactly one child folder. The chain stops at a branch, at a folder with
    /// its own Include rule, at a pinned folder and at a link.
    /// </summary>
    private NodeInfo Compact(NodeInfo folder)
    {
        var chain = new List<NodeInfo> { folder };
        var current = folder;
        while (!current.IsLink
               && _evaluator.RuleOf(current.RelativePath!) != VisibilityRule.Include
               && !_view.IsPinned(current.RelativePath!))
        {
            IReadOnlyList<NodeInfo> children;
            try
            {
                children = VisibleChildren(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            if (children is not [{ Kind: NodeKind.Folder } only] || _view.IsPinned(only.RelativePath!))
            {
                break;
            }

            chain.Add(only);
            current = only;
        }

        return chain.Count == 1
            ? folder
            : current with { Name = string.Join("/", chain.Select(c => c.Name)), Chain = chain };
    }

    private List<NodeInfo> FlatFiles(NodeInfo? under, bool skipPinned)
    {
        var files = new List<NodeInfo>();
        var stack = new Stack<NodeInfo?>();
        stack.Push(under);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            IReadOnlyList<NodeInfo> children;
            try
            {
                children = VisibleChildren(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var key = child.RelativePath!;
                if (skipPinned && _view.IsPinned(key))
                {
                    continue;
                }

                if (child.Kind == NodeKind.File)
                {
                    files.Add(child with { FlatDirectory = RelativeKey.Parent(key) ?? string.Empty });
                }
                else if (!child.IsLink)
                {
                    stack.Push(child);
                }
            }
        }

        return Sort(files);
    }

    private List<NodeInfo> Sort(List<NodeInfo> files)
    {
        var byName = files
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FlatDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal);
        if (_view.FlatSort != FlatSort.Custom || _view.FlatOrder.Count == 0)
        {
            return byName.ToList();
        }

        var index = new Dictionary<string, int>(RelativeKey.Comparer);
        for (var i = 0; i < _view.FlatOrder.Count; i++)
        {
            index.TryAdd(_view.FlatOrder[i], i);
        }

        return byName
            .Select((f, i) => (File: f, Rank: index.TryGetValue(f.RelativePath!, out var r) ? r : int.MaxValue, ByName: i))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.ByName)
            .Select(x => x.File)
            .ToList();
    }

    /// <summary>Pinned items that still exist, in pin order. Pinned files are shown whatever their rules say.</summary>
    private List<NodeInfo> PinnedNodes()
    {
        var nodes = new List<NodeInfo>();
        foreach (var key in _view.Pins.Distinct(RelativeKey.Comparer))
        {
            if (key.Length == 0)
            {
                continue;
            }

            // Excluded folders win over pins, like everywhere else.
            if (IsInExcludedFolder(key))
            {
                continue;
            }

            var full = Path.Combine(_all.RootPath!, key);
            FileSystemEntry? entry;
            try
            {
                entry = _store.GetEntry(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            var node = NodeInfo.FromEntry(entry, key);
            if (node.Kind == NodeKind.Folder && _view.Mode == ExplorerViewMode.Compact)
            {
                node = Compact(node);
            }

            nodes.Add(node with { IsPinned = true });
        }

        return nodes;
    }

    private bool IsInExcludedFolder(string key)
    {
        var excluded = ExcludedFolderPolicy.For(_all.Project);
        for (var folder = key; !string.IsNullOrEmpty(folder); folder = RelativeKey.Parent(folder))
        {
            // The pinned item itself only counts when it is a folder (checked with its entry below).
            if ((!RelativeKey.Comparer.Equals(folder, key) || _store.DirectoryExists(Path.Combine(_all.RootPath!, key))) && excluded.IsExcluded(folder))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<NodeInfo> List(NodeInfo? folder)
    {
        var key = folder?.RelativePath ?? string.Empty;
        if (_listed.TryGetValue(key, out var listed))
        {
            return listed;
        }

        // Once the budget is spent nothing more is listed (callers treat the error as "has content" / skip).
        if (_budget <= 0)
        {
            IsTruncated = true;
            throw new IOException("항목이 너무 많아 일부만 표시합니다.");
        }

        // The listing that crosses the budget is still used (a huge root shows its items); later ones are not made.
        var children = _all.GetChildren(folder);
        _budget -= children.Count;
        if (_budget < 0)
        {
            IsTruncated = true;
        }

        _listed[key] = children;
        return children;
    }
}
