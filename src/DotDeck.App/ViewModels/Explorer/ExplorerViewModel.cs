using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>Services shared by all explorers.</summary>
public sealed record ExplorerContext(
    ProjectRegistryGateway Registry,
    AppPaths Paths,
    IFileStore Store,
    IClipboardService Clipboard,
    IShellLauncher Shell,
    IDialogService Dialogs,
    IAppLog Log,
    Action<string> SetStatus);

/// <summary>
/// One project's explorer. Its rows live in a list (<see cref="List"/>: the project area, or its own detached
/// pane) as a contiguous segment that starts with <see cref="Header"/>. File system listing runs on the thread
/// pool; results are applied on the UI thread and discarded if a newer load started meanwhile.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly ExplorerContext _context;
    private readonly ProjectsPaneViewModel _owner;
    private readonly HashSet<string> _expanded = new(ExplorerTreeBuilder.KeyComparer);
    private int _loadVersion;
    private string? _pendingSelectionKey;

    public ExplorerViewModel(
        ExplorerContext context,
        ProjectsPaneViewModel owner,
        ExplorerListViewModel list,
        ProjectDefinition project,
        ExplorerViewState state)
    {
        _context = context;
        _owner = owner;
        List = list;
        Header = new ProjectHeaderRowViewModel(this) { IsCollapsed = state.IsCollapsed };
        _expanded.UnionWith(state.Expanded);
        _pendingSelectionKey = state.Selected;
        Project = project;
        Source = ProjectSources.Create(project, context.Paths, context.Store);
        ApplyHeader();
    }

    /// <summary>The list currently showing this explorer.</summary>
    public ExplorerListViewModel List { get; private set; }

    private ObservableCollection<ExplorerRowViewModel> Rows => List.Rows;

    /// <summary>True while shown in its own tool pane instead of the project area.</summary>
    [ObservableProperty]
    private bool _isDetached;

    public bool IsBuiltin => Project.IsBuiltin;

    public ProjectDefinition Project { get; private set; }

    public IProjectSource Source { get; private set; }

    public ProjectHeaderRowViewModel Header { get; }

    public string Id => Project.Id;

    /// <summary>Rows of this explorer below the header, in display order.</summary>
    public IEnumerable<ExplorerRowViewModel> BodyRows
    {
        get
        {
            var (start, end) = Segment();
            for (var i = start; i < end; i++)
            {
                yield return Rows[i];
            }
        }
    }

    public ExplorerViewState GetState()
    {
        var selectedKey = List.SelectedRow is NodeRowViewModel node && node.Explorer == this ? node.Key : _pendingSelectionKey;
        return new ExplorerViewState(Header.IsCollapsed, [.. _expanded], selectedKey, IsDetached);
    }

    /// <summary>Applies a changed definition (from the registry). Reloads when the tree content could differ.</summary>
    public async Task UpdateProjectAsync(ProjectDefinition project)
    {
        var old = Project;
        Project = project;
        ApplyHeader();
        var treeChanged = old.Filter != project.Filter
            || !string.Equals(old.Root, project.Root, StringComparison.OrdinalIgnoreCase)
            || !SameList(old.ExcludedFolders, project.ExcludedFolders);
        if (treeChanged)
        {
            Source = ProjectSources.Create(project, _context.Paths, _context.Store);
            ApplyHeader();
            await LoadAsync();
        }
    }

    /// <summary>(Re)builds the whole body from the file system, keeping expansion and selection.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (Header.IsCollapsed)
        {
            ReplaceBody([]);
            return;
        }

        Header.IsLoading = true;
        try
        {
            TreeBuildResult result;
            while (true)
            {
                var expanded = new HashSet<string>(_expanded, ExplorerTreeBuilder.KeyComparer);
                var source = Source;
                result = await Task.Run(() => ExplorerTreeBuilder.Build(source, expanded));
                if (version != _loadVersion)
                {
                    return;
                }

                // The user expanded/collapsed something meanwhile: build again from the current state
                // instead of applying the old snapshot.
                if (_expanded.SetEquals(expanded))
                {
                    break;
                }
            }

            // A refresh replaces the row objects: carry the selection as it is right now over by key.
            if (BodyRows.OfType<NodeRowViewModel>().FirstOrDefault(r => r.IsSelected) is { } selected)
            {
                _pendingSelectionKey = selected.Key;
            }

            _expanded.ExceptWith(result.StaleExpandedKeys);
            ReplaceBody(CreateBody(result));
            RestoreSelection();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version == _loadVersion)
            {
                _context.Log.Error($"프로젝트 트리를 불러오지 못했습니다: {Project.Name}", ex);
                ReplaceBody([new MessageRowViewModel(this, $"트리를 불러오지 못했습니다: {ex.Message}", isError: true)]);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                Header.IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task ToggleCollapsedAsync()
    {
        Header.IsCollapsed = !Header.IsCollapsed;
        await LoadAsync();
    }

    public async Task SetCollapsedAsync(bool collapsed)
    {
        if (Header.IsCollapsed != collapsed)
        {
            await ToggleCollapsedAsync();
        }
    }

    [RelayCommand]
    public async Task ToggleNodeAsync(NodeRowViewModel? row)
    {
        if (row is null || !row.IsFolder || row.IsLoading)
        {
            return;
        }

        if (row.IsExpanded)
        {
            Collapse(row);
            return;
        }

        _expanded.Add(row.Key);
        row.IsLoading = true;
        var version = _loadVersion;
        try
        {
            var expanded = new HashSet<string>(_expanded, ExplorerTreeBuilder.KeyComparer);
            var stale = new HashSet<string>(ExplorerTreeBuilder.KeyComparer);
            var source = Source;
            var children = await Task.Run(() => ExplorerTreeBuilder.BuildChildren(source, row.Node, row.Depth, expanded, stale));
            if (version != _loadVersion || row.IsExpanded || Rows.IndexOf(row) is var index && index < 0)
            {
                return;
            }

            _expanded.ExceptWith(stale);
            row.Error = null;
            row.IsExpanded = true;
            row.IsKnownEmpty = children.Count == 0;
            var i = Rows.IndexOf(row) + 1;
            foreach (var child in children)
            {
                Rows.Insert(i++, CreateNodeRow(child));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale attempt (row replaced by a refresh, or a newer load) must not touch current state.
            if (version == _loadVersion && Rows.Contains(row) && !row.IsExpanded)
            {
                _expanded.Remove(row.Key);
                row.Error = ex.Message;
                _context.SetStatus($"폴더를 열 수 없습니다: {ex.Message}");
            }
        }
        finally
        {
            row.IsLoading = false;
        }
    }

    public async Task ExpandAsync(NodeRowViewModel row)
    {
        if (!row.IsExpanded)
        {
            await ToggleNodeAsync(row);
        }
    }

    public void Collapse(NodeRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        // Nested expansion is remembered (VS behavior): only this folder's key is dropped.
        _expanded.Remove(row.Key);
        row.IsExpanded = false;
        var hidSelection = false;
        while (index + 1 < Rows.Count && Rows[index + 1] is NodeRowViewModel child && child.Explorer == this && child.Depth > row.Depth)
        {
            hidSelection |= child.IsSelected;
            Rows.RemoveAt(index + 1);
        }

        // Like a tree view: a selection inside the collapsed folder moves to the folder.
        if (hidSelection)
        {
            List.RequestSelection(this, row);
        }
    }

    [RelayCommand]
    private async Task CollapseAllAsync()
    {
        _expanded.Clear();
        if (Header.IsLoading)
        {
            // A load in flight would re-expand from its old snapshot: supersede it with a collapsed one.
            await LoadAsync();
            return;
        }

        // Invalidate pending folder expansions so a late result cannot reopen anything.
        ++_loadVersion;
        foreach (var row in BodyRows.OfType<NodeRowViewModel>().Where(r => r.Depth == 0 && r.IsExpanded).ToList())
        {
            Collapse(row);
        }
    }

    [RelayCommand]
    private Task ToggleFilterAsync()
    {
        var next = Project.Filter == ExplorerFilterMode.All ? ExplorerFilterMode.ConfigDocs : ExplorerFilterMode.All;
        var id = Project.Id;
        return _owner.ChangeRegistryAsync(r => r.SetFilter(id, next));
    }

    // Project management (header context menu); the project area owns dialogs and ordering.
    [RelayCommand]
    private Task EditProjectAsync() => _owner.EditProjectAsync(this);

    [RelayCommand]
    private Task HideProjectAsync() => _owner.ChangeRegistryAsync(r => r.SetHidden(Id, true));

    [RelayCommand]
    private Task MoveUpAsync() => _owner.MoveProjectAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _owner.MoveProjectAsync(this, +1);

    [RelayCommand]
    private Task RemoveProjectAsync() => _owner.RemoveProjectAsync(this);

    [RelayCommand]
    private void Detach() => _owner.Detach(this);

    [RelayCommand]
    private void Reattach() => _owner.Reattach(this);

    /// <summary>
    /// Moves this explorer's rows (header included) into <paramref name="target"/> at <paramref name="index"/>,
    /// keeping expansion, loaded rows and in-flight loads.
    /// </summary>
    public void MoveToList(ExplorerListViewModel target, int index)
    {
        var rows = SnapshotRows();
        var wasSelected = rows.FirstOrDefault(r => r.IsSelected);
        if (List.SelectedRow is { } selected && rows.Contains(selected))
        {
            List.SelectedRow = null;
        }

        RemoveSegment();
        List = target;
        for (var i = 0; i < rows.Count; i++)
        {
            target.Rows.Insert(Math.Min(index + i, target.Rows.Count), rows[i]);
        }

        if (wasSelected is not null)
        {
            target.Select(wasSelected);
        }
    }

    [RelayCommand]
    private void CopyRootPath()
    {
        if (Source.RootPath is { } root)
        {
            _context.SetStatus(_context.Clipboard.TrySetText(root) ? $"경로를 복사했습니다: {root}" : "클립보드를 사용할 수 없습니다.");
        }
    }

    [RelayCommand]
    private void RevealRoot()
    {
        if (Source.RootPath is { } root)
        {
            Reveal(root);
        }
    }

    public void Reveal(string path)
    {
        try
        {
            _context.Shell.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log.Error($"탐색기를 열지 못했습니다: {path}", ex);
            _context.SetStatus($"탐색기를 열지 못했습니다: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CreateRootAsync()
    {
        if (Source.RootPath is not { } root)
        {
            return;
        }

        try
        {
            _context.Store.CreateDirectory(root);
            _context.Log.Info($"프로젝트 폴더를 만들었습니다: {root}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.SetStatus($"폴더를 만들지 못했습니다: {ex.Message}");
            return;
        }

        await LoadAsync();
    }

    /// <summary>Removes this explorer for good: pending loads are discarded and its rows leave the list.</summary>
    public void RemoveRows()
    {
        ++_loadVersion;
        if (List.SelectedRow?.Explorer == this)
        {
            List.SelectedRow = null;
        }

        RemoveSegment();
    }

    /// <summary>Header plus body, for re-inserting the segment when projects are reordered.</summary>
    public List<ExplorerRowViewModel> SnapshotRows() => [Header, .. BodyRows];

    private void RemoveSegment()
    {
        var start = Rows.IndexOf(Header);
        if (start < 0)
        {
            return;
        }

        var (_, end) = Segment();
        for (var i = end - 1; i >= start; i--)
        {
            Rows.RemoveAt(i);
        }
    }

    private void ApplyHeader()
    {
        Header.Name = Project.Name;
        Header.Color = Project.Color;
        Header.RootPath = Source?.RootPath;
        Header.IsConfigDocsFilter = Project.Filter == ExplorerFilterMode.ConfigDocs;
    }

    private List<ExplorerRowViewModel> CreateBody(TreeBuildResult result)
    {
        if (result.RootState == ProjectRootState.Missing)
        {
            return [new MessageRowViewModel(this, "폴더 없음", actionText: "만들기", action: CreateRootCommand)];
        }

        if (result.RootError is { } error)
        {
            return [new MessageRowViewModel(this, $"폴더를 열 수 없습니다: {error}", isError: true)];
        }

        if (result.Rows.Count == 0)
        {
            return [new MessageRowViewModel(this, Project.Filter == ExplorerFilterMode.ConfigDocs ? "(표시할 설정·문서 파일 없음)" : "(비어 있음)")];
        }

        return [.. result.Rows.Select(CreateNodeRow)];
    }

    private NodeRowViewModel CreateNodeRow(TreeRow row) => new(this, row.Node, row.Depth, row.IsExpanded, row.Error);

    private void RestoreSelection()
    {
        if (_pendingSelectionKey is not { } key)
        {
            return;
        }

        var row = BodyRows.OfType<NodeRowViewModel>().FirstOrDefault(r => ExplorerTreeBuilder.KeyComparer.Equals(r.Key, key));
        _pendingSelectionKey = null;
        if (row is not null)
        {
            List.RequestSelection(this, row);
        }
    }

    private void ReplaceBody(IReadOnlyList<ExplorerRowViewModel> body)
    {
        var (start, end) = Segment();
        if (start < 0)
        {
            return;
        }

        for (var i = end - 1; i >= start; i--)
        {
            Rows.RemoveAt(i);
        }

        for (var i = 0; i < body.Count; i++)
        {
            Rows.Insert(start + i, body[i]);
        }
    }

    /// <summary>[start, end) of the body rows in the shared list; start is -1 when the header is not in the list.</summary>
    private (int Start, int End) Segment()
    {
        var header = Rows.IndexOf(Header);
        if (header < 0)
        {
            return (-1, -1);
        }

        var end = header + 1;
        while (end < Rows.Count && Rows[end] is not ProjectHeaderRowViewModel)
        {
            end++;
        }

        return (header + 1, end);
    }

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
}
