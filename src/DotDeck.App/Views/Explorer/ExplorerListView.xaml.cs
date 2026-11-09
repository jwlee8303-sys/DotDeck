using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DotDeck.App.ViewModels.Explorer;

namespace DotDeck.App.Views.Explorer;

/// <summary>
/// Explorer list view (project area and detached panes). Only view mechanics live here (keyboard routing,
/// in-place editors, drag and drop, scrolling); behavior is in <see cref="ExplorerListViewModel"/>.
/// </summary>
public partial class ExplorerListView : UserControl
{
    /// <summary>Marks drags started by DotDeck (moved by default; drags from Windows Explorer copy by default).</summary>
    private const string InternalDragFormat = "DotDeck.ExplorerDrag";

    private const double AutoScrollMargin = 18;

    private ExplorerListViewModel? _viewModel;
    private Point? _dragStart;
    private ExplorerRowViewModel? _pressedRow;
    private bool _deferredSelection;
    private ExplorerRowViewModel? _dropTarget;

    public ExplorerListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            _viewModel.EditorFocusRequested -= OnEditorFocusRequested;
        }

        _viewModel = e.NewValue as ExplorerListViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollIntoViewRequested += OnScrollIntoViewRequested;
            _viewModel.EditorFocusRequested += OnEditorFocusRequested;
        }
    }

    private void OnScrollIntoViewRequested(object? sender, ExplorerRowViewModel row)
    {
        Tree.ScrollIntoView(row);

        // Keep keyboard focus on the selected row so arrow keys continue from it (an in-place editor focuses itself).
        if (Tree.IsKeyboardFocusWithin && row is not IEditableRow { IsEditing: true })
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Tree.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item && !item.IsKeyboardFocusWithin)
                {
                    item.Focus();
                }
            });
        }
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel?.UpdateSelection(Tree.SelectedItems.OfType<ExplorerRowViewModel>());

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case ExplorerRowViewModel row:
                row.Explorer.List.RefreshCommandStates();
                break;
            case ExplorerListViewModel list:
                list.RefreshCommandStates();
                break;
        }
    }

    // ===== Keyboard =====

    private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Keys typed into an in-place name editor belong to the editor.
        if (_viewModel is null || e.OriginalSource is TextBox)
        {
            return;
        }

        var vm = _viewModel;
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as ExplorerRowViewModel ?? vm.SelectedRow;
        var handled = (Keyboard.Modifiers, e.Key) switch
        {
            (ModifierKeys.None, Key.Right) => Run(vm.ExpandOrDescendCommand, row),
            (ModifierKeys.None, Key.Left) => Run(vm.CollapseOrAscendCommand, row),
            (ModifierKeys.None, Key.Enter) => Run(vm.ActivateCommand, row),
            (ModifierKeys.None, Key.F2) => Run(vm.RenameCommand),
            (ModifierKeys.None, Key.Delete) => Run(vm.DeleteCommand),
            (ModifierKeys.Control, Key.C) => Run(vm.CopyCommand),
            (ModifierKeys.Control, Key.X) => Run(vm.CutCommand),
            (ModifierKeys.Control, Key.V) => Run(vm.PasteCommand),
            (ModifierKeys.Control, Key.D) => Run(vm.DuplicateCommand),
            (ModifierKeys.Control, Key.Z) => Run(vm.UndoCommand),
            (ModifierKeys.Control, Key.Y) => Run(vm.RedoCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Z) => Run(vm.RedoCommand),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.C) => Run(vm.CopyFullPathsCommand),
            _ => false,
        };

        e.Handled = handled;
    }

    /// <summary>Runs a command if it can run now. The key counts as handled either way (no beep, no fall-through).</summary>
    private static bool Run(ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }

        return true;
    }

    // ===== In-place name editor =====

    /// <summary>
    /// Focuses the in-place editor of <paramref name="row"/> once layout has run and a closing context menu has
    /// restored its own focus. Looked up through the container, because a recycled container reuses its editor.
    /// </summary>
    private void OnEditorFocusRequested(object? sender, ExplorerRowViewModel row)
    {
        Tree.ScrollIntoView(row);
        var attempts = 0;
        void TryFocus()
        {
            if (row is not IEditableRow { IsEditing: true })
            {
                return;
            }

            var box = Tree.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item
                ? FindDescendants<TextBox>(item).FirstOrDefault(b => b.IsVisible)
                : null;
            if (box is null || !box.Focus())
            {
                // Not realized yet, or focus taken back: try again a few times.
                if (++attempts < 5)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TryFocus);
                }

                return;
            }

            // Select the name without its extension (VS/Explorer).
            var text = box.Text;
            var extension = row is NodeRowViewModel { IsFolder: false } ? Path.GetExtension(text) : string.Empty;
            var stemLength = text.Length - extension.Length;
            box.Select(0, stemLength > 0 ? stemLength : text.Length);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TryFocus);
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox { DataContext: IEditableRow row })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                FocusTree();
                _ = _viewModel.CommitEditAsync(row);
                break;
            case Key.Escape:
                e.Handled = true;
                _viewModel.CancelEdit(row);
                FocusTree();
                break;
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere commits, like VS. (Enter/Esc already ended the edit, so this is then a no-op.)
        if (_viewModel is not null && sender is TextBox { DataContext: IEditableRow { IsEditing: true } row })
        {
            _ = _viewModel.CommitEditAsync(row);
        }
    }

    private void FocusTree()
    {
        if (_viewModel?.SelectedRow is { } selected && Tree.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            item.Focus();
        }
        else
        {
            Tree.Focus();
        }
    }

    // ===== Search box =====

    /// <summary>Esc clears the search; Down / Enter move to the first result.</summary>
    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when _viewModel.HasSearchText:
                _viewModel.ClearSearch();
                e.Handled = true;
                break;
            case Key.Down or Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                var first = _viewModel.Rows.OfType<NodeRowViewModel>().FirstOrDefault();
                if (first is not null)
                {
                    _viewModel.Select(first);
                }

                // Focus after layout so the row's container exists.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusTree);
                e.Handled = true;
                break;
        }
    }

    // ===== Header drop-downs =====

    /// <summary>A header button with a menu (view mode, "…" overflow): a left click opens the menu below it.</summary>
    private void OnDropDownButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button)
        {
            return;
        }

        // Opened from code, the menu does not pick up the button's data context by itself.
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ===== Mouse =====

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { DataContext: ExplorerRowViewModel row }
            && !IsInside<ButtonBase>(e.OriginalSource) && !IsInside<TextBox>(e.OriginalSource))
        {
            _viewModel?.ActivateCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>Chevron clicks toggle; the press also starts a possible drag.</summary>
    private void OnRowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = (sender as ListBoxItem)?.DataContext as ExplorerRowViewModel;
        if (e.ClickCount == 1 && e.OriginalSource is TextBlock { Name: "NodeChevron" or "HeaderChevron" })
        {
            switch (row)
            {
                case NodeRowViewModel node:
                    node.Explorer.ToggleNodeCommand.Execute(node);
                    break;
                case ProjectHeaderRowViewModel header:
                    header.Explorer.ToggleCollapsedCommand.Execute(null);
                    break;
            }

            return;
        }

        if (e.ClickCount != 1 || IsInside<TextBox>(e.OriginalSource) || IsInside<ButtonBase>(e.OriginalSource))
        {
            return;
        }

        _dragStart = e.GetPosition(Tree);
        _pressedRow = row;

        // Pressing an item of a multi-selection must not collapse the selection yet, or it could not be dragged
        // as a whole. The plain click is applied on release if no drag started.
        _deferredSelection = row is { IsSelected: true } && Tree.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None;
        if (_deferredSelection)
        {
            e.Handled = true;
            (sender as ListBoxItem)?.Focus();
        }
    }

    private void OnTreePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelection && _pressedRow is not null && Tree.Items.Contains(_pressedRow))
        {
            Tree.SelectedItem = _pressedRow;
        }

        // A single click only selects; files open on double-click or Enter.
        _deferredSelection = false;
        _dragStart = null;
        _pressedRow = null;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel is null || e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _pressedRow is null)
        {
            return;
        }

        var delta = e.GetPosition(Tree) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Drag the whole selection when the pressed row is part of it, otherwise just that row.
        var rows = _pressedRow.IsSelected ? _viewModel.SelectedRows : [_pressedRow];
        var paths = rows.OfType<NodeRowViewModel>().Where(r => r.IsFileSystemItem).Select(r => r.TargetNode.FullPath!).ToArray();
        _dragStart = null;
        _deferredSelection = false;
        if (paths.Length == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(InternalDragFormat, true);
        try
        {
            DragDrop.DoDragDrop(Tree, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
            _pressedRow = null;
        }
    }

    // ===== Drop =====

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        AutoScroll(e);
        if (FlatReorder(e) is not null)
        {
            e.Effects = DragDropEffects.Move & e.AllowedEffects;
            SetDropTarget(FindRow(e.OriginalSource));
            e.Handled = true;
            return;
        }

        var (paths, folder, row, copy) = Evaluate(e);
        e.Effects = folder is not null && ExplorerListViewModel.CanDrop(paths, folder, copy)
            ? (copy ? DragDropEffects.Copy : DragDropEffects.Move) & e.AllowedEffects
            : DragDropEffects.None;
        SetDropTarget(e.Effects == DragDropEffects.None ? null : row);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        e.Handled = true;
        if (FlatReorder(e) is var (explorer, dragged, before) && _viewModel is { } list)
        {
            e.Effects = DragDropEffects.Move;
            Dispatcher.BeginInvoke(() => list.ReorderFlatAsync(explorer, dragged, before));
            return;
        }

        var (paths, folder, _, copy) = Evaluate(e);
        if (_viewModel is null || folder is null || !ExplorerListViewModel.CanDrop(paths, folder, copy)
            || ((copy ? DragDropEffects.Copy : DragDropEffects.Move) & e.AllowedEffects) == 0)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;

        // Run after the drag loop has returned, so the source is not blocked by the operation.
        var vm = _viewModel;
        Dispatcher.BeginInvoke(() => vm.DropAsync(paths, folder, copy));
    }

    /// <summary>A DotDeck drag of flat-view files onto a file of the same flat list sorted by the user's order: reorder.</summary>
    private static (ExplorerViewModel Explorer, string[] Paths, NodeRowViewModel Before)? FlatReorder(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(InternalDragFormat) || (e.KeyStates & DragDropKeyStates.ControlKey) != 0
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || FindRow(e.OriginalSource) is not NodeRowViewModel row
            || ExplorerListViewModel.FlatReorderTarget(paths, row) is not { } explorer)
        {
            return null;
        }

        return (explorer, paths, row);
    }

    /// <summary>What a drop at the mouse position would do: dragged paths, target folder, row to highlight, copy?</summary>
    private static (string[] Paths, string? Folder, ExplorerRowViewModel? Row, bool Copy) Evaluate(DragEventArgs e)
    {
        var paths = e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
        var row = FindRow(e.OriginalSource);
        var folder = paths.Length == 0 ? null : ExplorerListViewModel.TargetFolderOf(row);

        // DotDeck's own drags move unless Ctrl is held; drags from other programs copy unless Shift is held.
        var isInternal = e.Data.GetDataPresent(InternalDragFormat);
        var ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        var shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        var copy = isInternal ? ctrl : !shift;

        // Highlight the folder that receives the drop (a file's own row is not a target).
        var highlight = row is NodeRowViewModel { IsFolder: false } ? null : row;
        return (paths, folder, highlight, copy);
    }

    private void SetDropTarget(ExplorerRowViewModel? row)
    {
        if (_dropTarget == row)
        {
            return;
        }

        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
        }

        _dropTarget = row;
        if (row is not null)
        {
            row.IsDropTarget = true;
        }
    }

    /// <summary>WPF does not scroll while dragging: scroll when the pointer is near the top or bottom edge.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(Tree) is not { } viewer)
        {
            return;
        }

        var y = e.GetPosition(Tree).Y;
        if (y < AutoScrollMargin)
        {
            viewer.LineUp();
        }
        else if (y > Tree.ActualHeight - AutoScrollMargin)
        {
            viewer.LineDown();
        }
    }

    // ===== Helpers =====

    private static ExplorerRowViewModel? FindRow(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is ListBoxItem { DataContext: ExplorerRowViewModel row })
            {
                return row;
            }
        }

        return null;
    }

    private static bool IsInside<T>(object source)
        where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null and not ListBoxItem; current = ParentOf(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current) =>
        current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
