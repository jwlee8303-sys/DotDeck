using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Settings;

namespace DotDeck.App.ViewModels;

public sealed partial class ShellViewModel
{
    public PathExclusionStore Exclusions { get; private set; } = null!;
    [RelayCommand]
    private void OpenExclusions() => _dialogs.ShowExclusions(new ExclusionSettingsViewModel(Exclusions,
        () => _dialogs.PickOpenFiles("제외할 파일 선택", null), () => _dialogs.PickFolder("제외할 폴더 선택", null)));

    private async Task<bool> AddExclusionAsync(ExclusionKind kind, string path)
    {
        try { await Task.Run(() => Exclusions.Add(kind, path)).ConfigureAwait(true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _dialogs.ShowError("제외항목 저장 실패", ex.Message); return false; }
    }

    private async Task<DiffReviewViewModel> ConfigureBackupExclusionsAsync(DiffReviewViewModel review)
    {
        foreach (var item in review.Items)
        {
            var excluded = await Task.Run(() => Exclusions.BackupPolicy.IsExcluded(item.Path)).ConfigureAwait(true);
            item.EnableBackupExclusion(path => AddExclusionAsync(ExclusionKind.AutomaticBackup, path), excluded);
        }
        return review;
    }
}
