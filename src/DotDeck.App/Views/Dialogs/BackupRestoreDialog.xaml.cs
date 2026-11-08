using System.Windows;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Views.Dialogs;

public partial class BackupRestoreDialog : Window
{
    public BackupRestoreDialog(BackupRestoreViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool restore) => DialogResult = restore;
}
