using System.Windows;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Views.Dialogs;

public partial class ProjectDialog : Window
{
    private readonly ProjectDialogViewModel _viewModel;

    public ProjectDialog(ProjectDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool committed)
    {
        DialogResult = committed;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Do not close underneath a save in progress (Esc / title bar X while committing).
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
