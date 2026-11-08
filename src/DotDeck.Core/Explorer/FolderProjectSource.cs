using DotDeck.Core.Files;
using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

/// <summary>
/// Every item of a folder-backed project except excluded folders (the edit-mode tree). Normal mode wraps it in
/// <see cref="Visibility.VisibleProjectSource"/>.
/// </summary>
public sealed class FolderProjectSource : IProjectSource
{
    private readonly IFileStore _store;
    private readonly ExcludedFolderPolicy _excluded;

    public FolderProjectSource(ProjectDefinition project, string rootPath, IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentNullException.ThrowIfNull(store);
        Project = project;
        RootPath = ProjectsFileSerializer.NormalizeRoot(rootPath);
        _store = store;
        _excluded = ExcludedFolderPolicy.For(project);
    }

    public ProjectDefinition Project { get; }

    public string RootPath { get; }

    string? IProjectSource.RootPath => RootPath;

    public ProjectRootState GetRootState() =>
        _store.DirectoryExists(RootPath) ? ProjectRootState.Ready : ProjectRootState.Missing;

    public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent)
    {
        string directory;
        string parentRelative;
        if (parent is null)
        {
            directory = RootPath;
            parentRelative = string.Empty;
        }
        else
        {
            if (parent.Kind != NodeKind.Folder || parent.FullPath is null || parent.RelativePath is null)
            {
                return [];
            }

            directory = parent.FullPath;
            parentRelative = parent.RelativePath;
        }

        var nodes = new List<NodeInfo>();
        foreach (var entry in _store.GetChildren(directory))
        {
            var relative = parentRelative.Length == 0 ? entry.Name : parentRelative + Path.DirectorySeparatorChar + entry.Name;
            if (entry.IsDirectory && _excluded.IsExcluded(relative))
            {
                continue;
            }

            nodes.Add(NodeInfo.FromEntry(entry, relative));
        }

        nodes.Sort(CompareForDisplay);
        return nodes;
    }

    /// <summary>Folders before files; then by name, case-insensitively (ordinal tiebreak for determinism).</summary>
    internal static int CompareForDisplay(NodeInfo a, NodeInfo b)
    {
        var byKind = (a.Kind == NodeKind.Folder ? 0 : 1).CompareTo(b.Kind == NodeKind.Folder ? 0 : 1);
        if (byKind != 0)
        {
            return byKind;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }
}
