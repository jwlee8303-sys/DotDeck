using System.IO;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Snapshots;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Settings;

namespace DotDeck.App.Tests;

public sealed class ExclusionSettingsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-exclusions-" + Guid.NewGuid().ToString("N"));
    public ExclusionSettingsTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, true); } catch (IOException) { } }
    private PathExclusionStore Store() => new(PhysicalFileStore.Instance, Path.Combine(_temp, "exclusions.json"));

    [Fact]
    public async Task One_dialog_manages_two_independent_lists_with_input_and_picker_actions()
    {
        var store = Store();
        var pickedFile = Path.Combine(_temp, "picked.md"); var pickedFolder = Path.Combine(_temp, "cache");
        var vm = new ExclusionSettingsViewModel(store, () => [pickedFile], () => pickedFolder);
        vm.Backup.PathText = Path.Combine(_temp, "typed.md");
        vm.Backup.AddCommand.Execute(null);
        vm.Backup.BrowseFolderCommand.Execute(null);
        vm.Review.BrowseFilesCommand.Execute(null);
        Assert.Empty(store.Get(ExclusionKind.AutomaticBackup)); // Editing is cancelable until Save.
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal(2, store.Get(ExclusionKind.AutomaticBackup).Count);
        Assert.Single(store.Get(ExclusionKind.ChangeReview));
        Assert.True(store.BackupPolicy.IsExcluded(Path.Combine(pickedFolder, "new.json")));
        Assert.False(store.ReviewPolicy.IsExcluded(Path.Combine(pickedFolder, "new.json")));
        Assert.True(store.ReviewPolicy.IsExcluded(pickedFile));
        Assert.False(store.BackupPolicy.IsExcluded(pickedFile));
        vm.Backup.Selected = vm.Backup.Items[0];
        vm.Backup.RemoveCommand.Execute(null); await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(store.Get(ExclusionKind.AutomaticBackup));
    }

    [Fact]
    public void Bad_input_is_reported_and_cancel_does_not_persist_edits()
    {
        var vm = new ExclusionSettingsViewModel(Store(), () => [], () => null);
        vm.Backup.PathText = "relative"; vm.Backup.AddCommand.Execute(null);
        Assert.NotNull(vm.Backup.Error); Assert.Empty(vm.Backup.Items);
        vm.Backup.PathText = Path.Combine(_temp, "valid.md"); vm.Backup.AddCommand.Execute(null);
        Assert.False(File.Exists(Path.Combine(_temp, "exclusions.json")));
    }

    [Fact]
    public void Backup_checkbox_registers_immediately_without_excluding_change_review()
    {
        var store = Store(); var path = Path.Combine(_temp, "file.md");
        var item = new DiffReviewItem(path, TextDiffService.Compute("before", "after"));
        item.EnableBackupExclusion(p => { store.Add(ExclusionKind.AutomaticBackup, p); return Task.FromResult(true); });
        var vm = new DiffReviewViewModel("save", "", [item], []);
        Assert.True(vm.ShowsFileList);
        item.IsBackupExcluded = true;
        Assert.True(store.BackupPolicy.IsExcluded(path));
        Assert.False(store.ReviewPolicy.IsExcluded(path));
        Assert.False(item.CanAddBackupExclusion);
    }

    [Fact]
    public void Review_checkbox_removes_the_item_and_revert_all_only_uses_remaining_rows()
    {
        var store = Store(); var a = Path.Combine(_temp, "a.md"); var b = Path.Combine(_temp, "b.md");
        var reverted = new List<string>();
        ChangeItem Item(string path) => new() { Path = path, Kind = ChangeKind.Created };
        var vm = new ChangeReviewViewModel("review", new ChangeSummary([Item(a), Item(b)], false),
            _ => new ChangeTexts("before", "after", null),
            (item, _) => { reverted.Add(item.Path); return new(item, RevertStatus.Reverted); }, (_, _) => true,
            path => { store.Add(ExclusionKind.ChangeReview, path); return Task.FromResult(true); });
        vm.Rows[0].IsExcluded = true;
        Assert.Equal(b, Assert.Single(vm.Rows).Path);
        Assert.True(store.ReviewPolicy.IsExcluded(a));
        Assert.False(store.BackupPolicy.IsExcluded(a));
        vm.RevertAllCommand.Execute(null);
        Assert.Equal([b], reverted);
    }

    [Fact]
    public async Task Saving_waits_for_pending_backup_exclusion_registration()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DiffReviewItem(Path.Combine(_temp, "a.md"), TextDiffService.Compute("a", "b"));
        item.EnableBackupExclusion(_ => pending.Task);
        var save = new DialogChoice("save", "save");
        var vm = new DiffReviewViewModel("save", "", [item], [save]);
        item.IsBackupExcluded = true;
        var choosing = vm.ChooseCommand.ExecuteAsync(save);
        Assert.False(choosing.IsCompleted); Assert.Null(vm.Result);
        pending.SetResult(true);
        await choosing;
        Assert.Equal("save", vm.Result);
    }

    [Fact]
    public void Failed_checkbox_registration_does_not_hide_the_row_or_leave_a_checked_box()
    {
        var item = new DiffReviewItem(Path.Combine(_temp, "a.md"), TextDiffService.Compute("a", "b"));
        item.EnableBackupExclusion(_ => Task.FromResult(false)); item.IsBackupExcluded = true;
        Assert.False(item.IsBackupExcluded);
        var change = new ChangeItem { Path = item.Path, Kind = ChangeKind.Created };
        var vm = new ChangeReviewViewModel("review", new([change], false), _ => new(null, null, null),
            (i, _) => new(i, RevertStatus.Reverted), (_, _) => true, _ => Task.FromResult(false));
        vm.Rows[0].IsExcluded = true;
        Assert.False(Assert.Single(vm.Rows).IsExcluded);
    }
}
