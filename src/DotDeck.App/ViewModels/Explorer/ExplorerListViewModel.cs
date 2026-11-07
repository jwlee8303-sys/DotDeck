using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>One clickable part of the breadcrumb above the tree.</summary>
public sealed record BreadcrumbSegment(string Name, ExplorerRowViewModel Target);

/// <summary>
/// A flattened, virtualized list of explorer sections with selection, breadcrumb and keyboard behavior.
/// Used by the project area (all attached projects) and by each detached explorer pane (one project).
/// </summary>
public sealed partial class ExplorerListViewModel : ObservableObject
{
    private readonly ExplorerContext _context;

    public ExplorerListViewModel(ExplorerContext context)
    {
        _context = context;
    }

    /// <summary>Raised when a row should be scrolled into view (the view owns the ListBox).</summary>
    public event EventHandler<ExplorerRowViewModel>? ScrollIntoViewRequested;

    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];

    [ObservableProperty]
    private ExplorerRowViewModel? _selectedRow;

    /// <summary>Full path of the selection, shown and copyable in the breadcrumb bar.</summary>
    [ObservableProperty]
    private string? _selectedPath;

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

    /// <summary>
    /// An explorer asks to select a row (restored selection, or the folder of a collapsed selection).
    /// A live selection elsewhere is not stolen; an empty, removed or same-explorer selection is replaced.
    /// </summary>
    public void RequestSelection(ExplorerViewModel explorer, ExplorerRowViewModel row)
    {
        if (SelectedRow is null || !Rows.Contains(SelectedRow) || SelectedRow.Explorer == explorer)
        {
            Select(row);
        }
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
}
