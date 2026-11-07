using System.Windows;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.App.Views.Dialogs;
using Microsoft.Win32;

namespace DotDeck.App.Infrastructure.Windows;

public sealed class DialogService : IDialogService
{
    private int _depth;

    public bool IsShowingDialog => _depth > 0;

    private T Modal<T>(Func<T> show)
    {
        _depth++;
        try
        {
            return show();
        }
        finally
        {
            _depth--;
        }
    }

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    public bool ShowProjectDialog(ProjectDialogViewModel viewModel)
    {
        var dialog = new ProjectDialog(viewModel) { Owner = Owner };
        return Modal(() => dialog.ShowDialog() == true);
    }

    public bool Confirm(string title, string message)
    {
        var result = Modal(() => Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
        return result == MessageBoxResult.Yes;
    }

    public SaveChangesChoice AskSaveChanges(string title, string message)
    {
        var result = Modal(() => Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes)
            : MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes));
        return result switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public string? ShowDiffReview(DiffReviewViewModel viewModel)
    {
        var dialog = new DiffReviewDialog(viewModel) { Owner = Owner };
        Modal(dialog.ShowDialog);
        return viewModel.Result;
    }

    public bool ShowBackupRestore(BackupRestoreViewModel viewModel)
    {
        var dialog = new BackupRestoreDialog(viewModel) { Owner = Owner };
        return Modal(dialog.ShowDialog) == true && viewModel.Chosen is not null;
    }

    public void ShowInfo(string title, string message)
    {
        if (Owner is { } owner)
        {
            Modal(() => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        }
        else
        {
            Modal(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }

    public void ShowError(string title, string message)
    {
        if (Owner is { } owner)
        {
            Modal(() => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error));
        }
        else
        {
            Modal(() => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }
}
