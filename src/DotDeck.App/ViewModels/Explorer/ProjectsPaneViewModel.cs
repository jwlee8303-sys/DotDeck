using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Explorer;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>One clickable part of the breadcrumb above the tree.</summary>
public sealed record BreadcrumbSegment(string Name, ExplorerRowViewModel Target);

/// <summary>
/// Project area (SPEC 2): every visible project as a section of one flattened, virtualized list.
/// </summary>
public sealed partial class ProjectsPaneViewModel : ToolPaneViewModel
{
    private readonly ExplorerContext _context;
    private readonly SessionStateStore _session;
    private readonly List<ExplorerViewModel> _explorers = [];
    private IReadOnlyDictionary<string, ExplorerViewState> _savedStates = new Dictionary<string, ExplorerViewState>();

    public ProjectsPaneViewModel(ExplorerContext context, SessionStateStore session)
        : base("tool.projects", "프로젝트", ToolLocation.Left)
    {
        _context = context;
        _session = session;
    }

    /// <summary>Raised when a row should be scrolled into view (the view owns the ListBox).</summary>
    public event EventHandler<ExplorerRowViewModel>? ScrollIntoViewRequested;

    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];

    public IReadOnlyList<ExplorerViewModel> Explorers => _explorers;

    [ObservableProperty]
    private ExplorerRowViewModel? _selectedRow;

    /// <summary>Full path of the selection, shown and copyable in the breadcrumb bar.</summary>
    [ObservableProperty]
    private string? _selectedPath;

    /// <summary>Restores the saved state, shows every visible project and loads the trees (SPEC 2: auto open).</summary>
    public async Task InitializeAsync()
    {
        _savedStates = _session.LoadExplorers();
        _context.Registry.Changed += OnRegistryChanged;
        await SyncProjectsAsync();
    }

    public void SaveState()
    {
        var states = new Dictionary<string, ExplorerViewState>(StringComparer.Ordinal);

        // Keep the state of hidden projects so that showing them again restores it.
        foreach (var (id, state) in _savedStates)
        {
            states[id] = state;
        }

        foreach (var explorer in _explorers)
        {
            states[explorer.Id] = explorer.GetState(SelectedRow);
        }

        // Drop state of projects that no longer exist.
        foreach (var id in states.Keys.ToList())
        {
            if (_context.Registry.Find(id) is null)
            {
                states.Remove(id);
            }
        }

        _session.SaveExplorers(states);
    }

    [RelayCommand]
    private async Task ActivateAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel header:
                await header.Explorer.ToggleCollapsedCommand.ExecuteAsync(null);
                break;
            case NodeRowViewModel { IsFolder: true } folder:
                await folder.Explorer.ToggleNodeAsync(folder);
                break;
            case NodeRowViewModel { Node.IsSecret: true } secret:
                _context.SetStatus($"비밀 파일은 열 수 없습니다: {secret.Name}");
                break;
            case NodeRowViewModel { Node.CanOpen: true } file:
                // The editor arrives in Phase 1c.
                _context.SetStatus($"편집기는 다음 단계(Phase 1c)에서 제공됩니다: {file.Node.FullPath}");
                break;
            case MessageRowViewModel { Action: { } action }:
                action.Execute(null);
                break;
        }
    }

    /// <summary>Right arrow: expand, or move to the first child when already expanded.</summary>
    [RelayCommand]
    private async Task ExpandOrDescendAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel { IsCollapsed: true } header:
                await header.Explorer.SetCollapsedAsync(false);
                break;
            case ProjectHeaderRowViewModel header:
                Select(header.Explorer.BodyRows.FirstOrDefault());
                break;
            case NodeRowViewModel { IsFolder: true, IsExpanded: false } folder:
                await folder.Explorer.ExpandAsync(folder);
                break;
            case NodeRowViewModel { IsExpanded: true } folder:
                var next = Rows.IndexOf(folder) + 1;
                if (next < Rows.Count && Rows[next] is NodeRowViewModel child && child.Depth > folder.Depth)
                {
                    Select(child);
                }

                break;
        }
    }

    /// <summary>Left arrow: collapse, or move to the parent.</summary>
    [RelayCommand]
    private async Task CollapseOrAscendAsync(ExplorerRowViewModel? row)
    {
        switch (row)
        {
            case ProjectHeaderRowViewModel { IsCollapsed: false } header:
                await header.Explorer.SetCollapsedAsync(true);
                break;
            case NodeRowViewModel { IsExpanded: true } folder:
                folder.Explorer.Collapse(folder);
                break;
            case NodeRowViewModel node:
                Select(FindParent(node));
                break;
            case MessageRowViewModel message:
                Select(message.Explorer.Header);
                break;
        }
    }

    [RelayCommand]
    private void SelectBreadcrumb(BreadcrumbSegment? segment) => Select(segment?.Target);

    [RelayCommand]
    private void CopySelectedPath()
    {
        if (SelectedPath is { } path)
        {
            _context.SetStatus(_context.Clipboard.TrySetText(path) ? $"경로를 복사했습니다: {path}" : "클립보드를 사용할 수 없습니다.");
        }
    }

    public void Select(ExplorerRowViewModel? row)
    {
        if (row is null || !Rows.Contains(row))
        {
            return;
        }

        foreach (var other in Rows)
        {
            if (other != row && other.IsSelected)
            {
                other.IsSelected = false;
            }
        }

        row.IsSelected = true;
        SelectedRow = row;
        ScrollIntoViewRequested?.Invoke(this, row);
    }

    partial void OnSelectedRowChanged(ExplorerRowViewModel? value) => UpdateBreadcrumb(value);

    private void UpdateBreadcrumb(ExplorerRowViewModel? row)
    {
        Breadcrumb.Clear();
        SelectedPath = null;
        if (row is null)
        {
            return;
        }

        var explorer = row.Explorer;
        Breadcrumb.Add(new BreadcrumbSegment(explorer.Header.Name, explorer.Header));
        if (row is not NodeRowViewModel node)
        {
            SelectedPath = explorer.Source.RootPath;
            return;
        }

        // Ancestors of a visible row are always visible (expanded) rows just above it.
        var chain = new Stack<NodeRowViewModel>();
        for (NodeRowViewModel? current = node; current is not null; current = FindParent(current) as NodeRowViewModel)
        {
            chain.Push(current);
        }

        foreach (var part in chain)
        {
            Breadcrumb.Add(new BreadcrumbSegment(part.Name, part));
        }

        SelectedPath = node.Node.FullPath ?? node.Name;
    }

    /// <summary>The nearest row above with a smaller depth, or the project header.</summary>
    private ExplorerRowViewModel? FindParent(NodeRowViewModel node)
    {
        var index = Rows.IndexOf(node);
        for (var i = index - 1; i >= 0; i--)
        {
            switch (Rows[i])
            {
                case ProjectHeaderRowViewModel header:
                    return header;
                case NodeRowViewModel candidate when candidate.Depth < node.Depth:
                    return candidate;
            }
        }

        return null;
    }

    private async void OnRegistryChanged(object? sender, EventArgs e)
    {
        try
        {
            await SyncProjectsAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Error("프로젝트 목록을 갱신하지 못했습니다.", ex);
        }
    }

    /// <summary>Creates, updates, removes and reorders explorers to match the registry.</summary>
    private async Task SyncProjectsAsync()
    {
        var wanted = _context.Registry.Projects.Where(p => !p.Hidden).ToList();
        var loads = new List<Task>();

        foreach (var explorer in _explorers.Where(x => !wanted.Any(p => p.Id == x.Id)).ToList())
        {
            // Remember the state so the project reopens the same way when shown again.
            _savedStates = new Dictionary<string, ExplorerViewState>(_savedStates) { [explorer.Id] = explorer.GetState(SelectedRow) };
            if (SelectedRow?.Explorer == explorer)
            {
                SelectedRow = null;
            }

            explorer.SelectionRequested -= OnSelectionRequested;
            explorer.RemoveRows();
            _explorers.Remove(explorer);
        }

        var ordered = new List<ExplorerViewModel>();
        foreach (var project in wanted)
        {
            var existing = _explorers.FirstOrDefault(x => x.Id == project.Id);
            if (existing is not null)
            {
                if (!Equals(existing.Project, project))
                {
                    loads.Add(existing.UpdateProjectAsync(project));
                }

                ordered.Add(existing);
                continue;
            }

            ExplorerViewModel created;
            try
            {
                created = new ExplorerViewModel(_context, Rows, project, _savedStates.GetValueOrDefault(project.Id, ExplorerViewState.Default));
            }
            catch (ArgumentException ex)
            {
                _context.Log.Error($"프로젝트를 표시할 수 없습니다: {project.Name}", ex);
                continue;
            }

            created.SelectionRequested += OnSelectionRequested;
            ordered.Add(created);
            loads.Add(created.LoadAsync());
        }

        ApplyOrder(ordered);
        _explorers.Clear();
        _explorers.AddRange(ordered);
        await Task.WhenAll(loads);
    }

    /// <summary>Lays the segments out in <paramref name="ordered"/> order, moving only when needed.</summary>
    private void ApplyOrder(List<ExplorerViewModel> ordered)
    {
        var current = Rows.OfType<ProjectHeaderRowViewModel>().Select(h => h.Explorer).ToList();
        if (ordered.Take(current.Count).SequenceEqual(current))
        {
            // Same order; just append new explorers (headers only, bodies load asynchronously).
            foreach (var explorer in ordered.Skip(current.Count))
            {
                Rows.Add(explorer.Header);
            }

            return;
        }

        var segments = current.ToDictionary(x => x, x => x.SnapshotRows());
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var explorer in ordered)
        {
            foreach (var row in segments.TryGetValue(explorer, out var rows) ? rows : [explorer.Header])
            {
                Rows.Add(row);
            }
        }

        if (selected is not null && Rows.Contains(selected))
        {
            Select(selected);
        }
    }

    private void OnSelectionRequested(object? sender, ExplorerRowViewModel row)
    {
        // Restoring must not steal a live selection the user made elsewhere; it only fills an empty one or
        // replaces a row that was just removed (refresh, collapse) or one in the requesting explorer.
        if (SelectedRow is null || !Rows.Contains(SelectedRow) || SelectedRow.Explorer == sender)
        {
            Select(row);
        }
    }
}
