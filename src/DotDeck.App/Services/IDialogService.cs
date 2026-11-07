using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.Services;

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Modal UI for view models (keeps Window types out of them).</summary>
public interface IDialogService
{
    /// <summary>True while any dialog of this service is open (nested message loops run timers and posts).</summary>
    bool IsShowingDialog { get; }

    /// <summary>Shows the project add/edit dialog. Returns true when it was committed.</summary>
    bool ShowProjectDialog(ProjectDialogViewModel viewModel);

    /// <summary>Asks a yes/no question; the default (Enter) answer is "no" for destructive questions.</summary>
    bool Confirm(string title, string message);

    /// <summary>Asks whether to save unsaved changes before closing. Default (Enter) is Save.</summary>
    SaveChangesChoice AskSaveChanges(string title, string message);

    /// <summary>Shows a diff review; returns the chosen <see cref="DialogChoice.Id"/> or null when dismissed.</summary>
    string? ShowDiffReview(DiffReviewViewModel viewModel);

    /// <summary>Shows the backup list of a file. True when the user chose a backup to restore.</summary>
    bool ShowBackupRestore(BackupRestoreViewModel viewModel);

    /// <summary>Shows an informational message.</summary>
    void ShowInfo(string title, string message);

    /// <summary>Shows an error message.</summary>
    void ShowError(string title, string message);

    /// <summary>
    /// Asks for a line of text. <paramref name="validate"/> returns an error message or null. Returns the trimmed
    /// text, or null when cancelled.
    /// </summary>
    string? AskText(string title, string prompt, string initial, Func<string, string?> validate);

    /// <summary>File picker allowing several files. Returns an empty list when cancelled.</summary>
    IReadOnlyList<string> PickOpenFiles(string title, string? initialDirectory);

    /// <summary>Save-as picker (asks before replacing an existing file). Returns null when cancelled.</summary>
    string? PickSaveFile(string title, string defaultFileName, string? initialDirectory);

    /// <summary>Folder picker. Returns null when cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);

    /// <summary>Settings dialog. Returns the new settings, or null when cancelled.</summary>
    DotDeck.Core.Settings.AppSettings? ShowSettings(DotDeck.Core.Settings.AppSettings current, string defaultBackupDirectory);

    /// <summary>Project backup dialog: returns the chosen project ids and destination folder, or null.</summary>
    (IReadOnlyList<string> ProjectIds, string Destination)? ShowProjectBackup(
        IReadOnlyList<(string Id, string Name, string Root)> projects, string? lastDestination);
}
