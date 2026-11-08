using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DotDeck.App.ViewModels.Tools;

namespace DotDeck.App.Views.Tools;

public partial class SearchPaneView : UserControl
{
    public SearchPaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SearchResultsPaneViewModel old)
            {
                old.FocusRequested -= OnFocusRequested;
            }

            if (e.NewValue is SearchResultsPaneViewModel vm)
            {
                vm.FocusRequested += OnFocusRequested;
            }
        };
        Loaded += (_, _) => OnFocusRequested(this, EventArgs.Empty);
    }

    // Deferred until the docking manager has finished activating the pane, which would otherwise take focus back.
    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (DataContext is SearchResultsPaneViewModel { FocusPending: true } vm && IsVisible)
            {
                vm.FocusPending = false;
                Keyboard.Focus(QueryBox);
                QueryBox.SelectAll();
            }
        });

    private void OnResultActivated(object sender, MouseButtonEventArgs e) => Open();

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open();
            e.Handled = true;
        }
    }

    private void Open()
    {
        if (DataContext is SearchResultsPaneViewModel { Selected: { } hit } vm)
        {
            vm.OpenCommand.Execute(hit);
        }
    }
}
