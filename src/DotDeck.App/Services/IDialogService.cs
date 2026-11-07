using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Services;

/// <summary>Modal UI for view models (keeps Window types out of them).</summary>
public interface IDialogService
{
    /// <summary>Shows the project add/edit dialog. Returns true when it was committed.</summary>
    bool ShowProjectDialog(ProjectDialogViewModel viewModel);

    /// <summary>Asks a yes/no question; the default (Enter) answer is "no" for destructive questions.</summary>
    bool Confirm(string title, string message);

    /// <summary>Shows an error message.</summary>
    void ShowError(string title, string message);

    /// <summary>Folder picker. Returns null when cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);
}
