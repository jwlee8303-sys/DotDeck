using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Dialogs;

namespace DotDeck.App.ViewModels;

/// <summary>Application updates: background check after startup and Help > "업데이트 확인".</summary>
public partial class ShellViewModel
{
    private const int MaxNotesLength = 1500;
    private AppUpdateCoordinator? _updates;

    /// <summary>Raised when the app should close without asking again (an update is applied on exit).</summary>
    public event EventHandler? ExitRequested;

    public void AttachUpdater(IAppUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        _updates = new AppUpdateCoordinator(updater, new UpdateUi(this), _log);
    }

    /// <summary>Called once the main window is ready; never shows anything unless a newer release exists.</summary>
    public Task CheckForUpdatesOnStartupAsync() => _updates?.CheckAsync(UpdateCheckTrigger.Startup) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CheckForUpdates() => _updates?.CheckAsync(UpdateCheckTrigger.Manual) ?? Task.CompletedTask;

    public static string FormatUpdateMessage(AppUpdateOffer offer)
    {
        var notes = offer.Notes?.Trim();
        if (notes is { Length: > MaxNotesLength })
        {
            notes = notes[..MaxNotesLength].TrimEnd() + "\n…";
        }

        var message = $"새 버전이 있습니다.\n\n현재 버전: {offer.CurrentVersion}\n새 버전: {offer.NewVersion}";
        return string.IsNullOrEmpty(notes) ? message : $"{message}\n\n변경 사항\n{notes}";
    }

    private sealed class UpdateUi(ShellViewModel shell) : IUpdateUi
    {
        private const string Title = "DotDeck 업데이트";

        public bool AskInstall(AppUpdateOffer offer) =>
            shell._dialogs.AskChoice(Title, FormatUpdateMessage(offer),
            [
                new DialogChoice("now", "지금 업데이트", IsDefault: true),
                new DialogChoice("later", "나중에", IsCancel: true),
            ]).Choice == "now";

        public void ShowInfo(string message) => shell._dialogs.ShowInfo(Title, message);

        public void ShowError(string message) => shell._dialogs.ShowError(Title, message);

        public void SetStatus(string message) => shell.StatusMessage = message;

        public Task<bool> ConfirmExitAsync() => shell.ConfirmCloseAllAsync();

        public void RequestExit() => shell.ExitRequested?.Invoke(shell, EventArgs.Empty);
    }
}
