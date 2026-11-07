using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotDeck.App.ViewModels.Explorer;

namespace DotDeck.App.Views.Explorer;

/// <summary>
/// Explorer list view (project area and detached panes). Only view mechanics live here (keyboard routing, double-click, scrolling);
/// behavior is in <see cref="ExplorerListViewModel"/>.
/// </summary>
public partial class ExplorerListView : UserControl
{
    private ExplorerListViewModel? _viewModel;

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
        }

        _viewModel = e.NewValue as ExplorerListViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollIntoViewRequested += OnScrollIntoViewRequested;
        }
    }

    private void OnScrollIntoViewRequested(object? sender, ExplorerRowViewModel row)
    {
        Tree.ScrollIntoView(row);

        // Keep keyboard focus on the selected row so arrow keys continue from it.
        if (Tree.IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Tree.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
                {
                    item.Focus();
                }
            });
        }
    }

    private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var row = (e.OriginalSource as FrameworkElement)?.DataContext as ExplorerRowViewModel ?? _viewModel.SelectedRow;
        switch (e.Key)
        {
            case Key.Right:
                _viewModel.ExpandOrDescendCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.Left:
                _viewModel.CollapseOrAscendCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.ActivateCommand.Execute(row);
                e.Handled = true;
                break;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { DataContext: ExplorerRowViewModel row } && !IsInsideButton(e))
        {
            _viewModel?.ActivateCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>A single click on a chevron toggles, like a tree view's expander.</summary>
    private void OnRowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not TextBlock { Name: "NodeChevron" or "HeaderChevron" })
        {
            return;
        }

        switch ((sender as ListBoxItem)?.DataContext)
        {
            case NodeRowViewModel node:
                node.Explorer.ToggleNodeCommand.Execute(node);
                break;
            case ProjectHeaderRowViewModel header:
                header.Explorer.ToggleCollapsedCommand.Execute(null);
                break;
        }
    }

    private static bool IsInsideButton(MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null and not ListBoxItem;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
