using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>Save all, external change handling (reload / overwrite / keep), backup restore.</summary>
public sealed partial class ShellViewModel
{
    private const string DeletedMarker = "<deleted>";

    private readonly Dictionary<string, IDisposable> _contentWatchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _skipSavePreview;
    private bool _checkingExternal;
    private bool _checkAgain;
    private bool _deepCheck;

    /// <summary>
    /// Shows the disk version against the editor text and asks what to do. Returns "overwrite", "reload" or null.
    /// </summary>
    private async Task<string?> ResolveConflictAsync(DocumentBuffer buffer, ExternalChangeKind kind, string myText, bool onSave)
    {
        string diskText;
        string? note = null;
        try
        {
            diskText = kind == ExternalChangeKind.Deleted
                ? string.Empty
                : (await Task.Run(() => _buffers.Reader.Read(buffer.Path)).ConfigureAwait(true)).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diskText = string.Empty;
            note = "디스크 내용을 읽을 수 없음";
        }

        var deleted = kind == ExternalChangeKind.Deleted || note is not null;
        var what = kind switch
        {
            ExternalChangeKind.Deleted => "디스크에서 삭제되었습니다",
            ExternalChangeKind.Created => "다른 프로그램이 같은 이름의 파일을 만들었습니다",
            _ => "다른 프로그램이 파일을 변경했습니다",
        };
        List<DialogChoice> choices = [new("overwrite", onSave ? "내 내용으로 덮어쓰기" : "내 내용 유지")];
        if (!deleted)
        {
            choices.Add(new DialogChoice("reload", "디스크 내용 다시 불러오기"));
        }

        choices.Add(new DialogChoice("cancel", onSave ? "저장 취소" : "나중에", IsDefault: true, IsCancel: true));
        var review = new DiffReviewViewModel(
            "외부 변경",
            $"{buffer.Path}\n{what}. (−: 디스크, +: 편집 중인 내용){(onSave ? "\n덮어쓰면 현재 디스크 내용은 백업됩니다." : string.Empty)}",
            [new DiffReviewItem(buffer.Path, TextDiffService.Compute(diskText, myText), note)],
            choices);
        return _dialogs.ShowDiffReview(review) switch
        {
            "overwrite" => "overwrite",
            "reload" => "reload",
            _ => null,
        };
    }

    /// <summary>
    /// Reads the disk version and replaces the buffer text, unless the text changed (typing) or the buffer was closed
    /// while reading: those edits were never offered for discarding.
    /// </summary>
    /// <param name="expectedChangeCount">Change count of the text the user saw when choosing to reload.</param>
    private async Task ReloadAsync(DocumentBuffer buffer, long? expectedChangeCount = null)
    {
        var changeCount = expectedChangeCount ?? buffer.ChangeCount;
        try
        {
            var snapshot = await Task.Run(() => _buffers.Reader.Read(buffer.Path)).ConfigureAwait(true);
            if (buffer.ChangeCount != changeCount || _buffers.Find(buffer.Path) != buffer)
            {
                StatusMessage = $"편집 중이어서 다시 불러오지 않았습니다: {buffer.FileName}";
                return;
            }

            buffer.Reload(snapshot);
            StatusMessage = $"디스크 내용을 다시 불러왔습니다: {buffer.FileName} (Ctrl+Z로 되돌릴 수 있음)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"다시 불러오지 못했습니다: {buffer.FileName} — {ex.Message}";
            _log.Warning($"Reload failed: {buffer.Path} ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Compares every open buffer with the disk (window activation, file watcher). Clean buffers reload silently;
    /// edited ones ask once per disk version.
    /// </summary>
    public async Task CheckExternalChangesAsync(bool deep = false)
    {
        _deepCheck |= deep;

        // Never stack a conflict dialog over another modal (save preview, restore, ...): the main window is
        // activated again when that closes, which re-runs this check.
        if (_dialogs.IsShowingDialog)
        {
            return;
        }

        if (_checkingExternal)
        {
            _checkAgain = true;
            return;
        }

        _checkingExternal = true;
        try
        {
            do
            {
                _checkAgain = false;
                var force = _deepCheck;
                _deepCheck = false;
                foreach (var buffer in _buffers.Buffers.ToList())
                {
                    await CheckBufferAsync(buffer, force).ConfigureAwait(true);
                }
            }
            while (_checkAgain);
        }
        finally
        {
            _checkingExternal = false;
        }
    }

    private async Task CheckBufferAsync(DocumentBuffer buffer, bool force)
    {
        // A running save owns the file; its own write is recorded in the snapshot when it finishes.
        if (buffer.SaveLock.CurrentCount == 0 || _buffers.Find(buffer.Path) != buffer)
        {
            return;
        }

        var baseline = buffer.Snapshot;
        DiskCheck check;
        try
        {
            check = await Task.Run(() => ExternalChangeDetector.Check(_store, baseline, force)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Skip if the buffer was saved, reloaded or closed while checking.
        if (check.State == DiskState.Unchanged || buffer.Snapshot != baseline || _buffers.Find(buffer.Path) != buffer
            || buffer.SaveLock.CurrentCount == 0)
        {
            return;
        }

        var diskKey = check.State == DiskState.Deleted ? DeletedMarker : check.Sha256;
        if (diskKey == buffer.DismissedDiskHash)
        {
            return;
        }

        if (check.State == DiskState.Deleted)
        {
            buffer.DismissedDiskHash = DeletedMarker;
            StatusMessage = $"디스크에서 삭제된 파일입니다: {buffer.Path} (저장하면 다시 만듭니다)";
            return;
        }

        if (!buffer.IsDirty)
        {
            await ReloadAsync(buffer).ConfigureAwait(true);
            return;
        }

        var shownChangeCount = buffer.ChangeCount;
        switch (await ResolveConflictAsync(buffer, ExternalChangeKind.Modified, buffer.Document.Text, onSave: false).ConfigureAwait(true))
        {
            case "reload":
                await ReloadAsync(buffer, shownChangeCount).ConfigureAwait(true);
                break;
            default:
                // Keep editing; do not ask again for this disk version. Saving later still detects the conflict.
                buffer.DismissedDiskHash = diskKey;
                break;
        }
    }

    /// <summary>Watches the folders of open files (debounced) and re-checks on change.</summary>
    private void UpdateContentWatchers()
    {
        var wanted = _buffers.Buffers
            .Select(b => Path.GetDirectoryName(b.Path))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _contentWatchers.Keys.Where(d => !wanted.Contains(d)).ToList())
        {
            _contentWatchers[directory].Dispose();
            _contentWatchers.Remove(directory);
        }

        foreach (var directory in wanted.Where(d => !_contentWatchers.ContainsKey(d)))
        {
            if (FolderWatcherFactory.WatchContent(directory, OnContentChanged) is { } watcher)
            {
                _contentWatchers[directory] = watcher;
            }
        }
    }

    // Runs on a thread-pool thread (debouncer).
    private void OnContentChanged()
    {
        if (_ui is { } ui)
        {
            ui.Post(_ => _ = CheckExternalChangesAsync(deep: true), null);
        }
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        var hadUntitled = HasUnsavedUntitled;
        if (!await SaveAllUntitledAsync().ConfigureAwait(true))
        {
            return;
        }

        var dirty = _buffers.Buffers.Where(b => b.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            if (!hadUntitled)
            {
                StatusMessage = "저장할 변경 내용이 없습니다.";
            }

            return;
        }

        // Plan everything first so one review shows all files; blocked files are reported and skipped.
        var plans = new List<(DocumentBuffer Buffer, WritePlan Plan)>();
        var blocked = new List<string>();
        foreach (var buffer in dirty)
        {
            var text = buffer.Document.Text;
            var baseline = buffer.Snapshot;
            var plan = await Task.Run(() => SafeWriter.Prepare(buffer.Path, baseline, text)).ConfigureAwait(true);
            if (plan.CanCommit)
            {
                plans.Add((buffer, plan));
            }
            else
            {
                blocked.Add($"{buffer.Path}\n  {DescribeBlocked(plan)}");
            }
        }

        if (blocked.Count > 0)
        {
            _dialogs.ShowError("저장할 수 없는 파일", string.Join("\n\n", blocked));
        }

        var changed = plans.Where(p => !p.Plan.IsUnchanged).ToList();
        if (changed.Count > 0 && !_skipSavePreview)
        {
            var review = new DiffReviewViewModel(
                "모두 저장",
                $"{changed.Count}개 파일을 저장합니다. 각 파일의 현재 디스크 내용은 백업됩니다.",
                changed.Select(p => ToReviewItem(p.Plan)),
                [new DialogChoice("save", "모두 저장", IsDefault: true), new DialogChoice("cancel", "취소", IsCancel: true)])
            {
                SkipLabel = "이번 실행 동안 저장 전 미리보기 생략",
            };
            if (_dialogs.ShowDiffReview(review) != "save")
            {
                StatusMessage = "저장을 취소했습니다.";
                return;
            }

            _skipSavePreview |= review.SkipNextTime;
        }

        // Commit exactly what was reviewed; edits made since then stay unsaved (the buffer remains dirty).
        var saved = 0;
        foreach (var (buffer, plan) in plans)
        {
            if (await SaveBufferAsync(buffer, preview: false, plan).ConfigureAwait(true))
            {
                saved++;
            }
        }

        StatusMessage = $"{saved}/{dirty.Count}개 파일을 저장했습니다.";
    }

    [RelayCommand(CanExecute = nameof(CanUseActiveTextDocument))]
    private async Task RestoreBackupAsync()
    {
        if (ActiveTextDocument is not { } document)
        {
            return;
        }

        var buffer = document.Buffer;
        IReadOnlyList<BackupEntry> entries;
        try
        {
            entries = await Task.Run(() => _backups.ListFor(buffer.Path).Select(e => new BackupEntry(e.Set, e.File)).ToList())
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("백업 목록", ex.Message);
            return;
        }

        var viewModel = new BackupRestoreViewModel(
            buffer.Path,
            entries,
            buffer.Document.Text,
            entry => TextFileReader.Decode(buffer.Path, _backups.ReadContent(entry.Set, entry.File), default).Text);
        if (!_dialogs.ShowBackupRestore(viewModel) || viewModel.Chosen is not { } chosen)
        {
            return;
        }

        if (buffer.IsDirty
            && !_dialogs.Confirm("백업에서 복원", "편집 중인 변경 내용이 백업 내용으로 바뀝니다. (복원 후 Ctrl+Z로 되돌릴 수 있습니다)\n\n계속할까요?"))
        {
            return;
        }

        var changeCount = buffer.ChangeCount;
        await buffer.SaveLock.WaitAsync().ConfigureAwait(true);
        try
        {
            var result = await Task.Run(() => _writer.Restore(chosen.Set, chosen.File)).ConfigureAwait(true);
            if (buffer.ChangeCount != changeCount)
            {
                // Typed while restoring: keep that text (unsaved) instead of replacing it without asking.
                buffer.AcceptDiskSnapshot(result.Snapshot);
                StatusMessage = $"복원했지만 그 사이 편집한 내용이 있어 편집기 내용은 유지했습니다: {buffer.FileName}";
                return;
            }

            buffer.Reload(result.Snapshot);
            if (result.Backup is { } before)
            {
                LastBackupText = $"백업: {before.Directory}";
            }

            StatusMessage = $"{chosen.When} 백업으로 복원했습니다: {buffer.FileName}";
            _log.Info($"Restored {buffer.Path} from backup {chosen.Set.Id}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var message = ex is ReadOnlyFileException ? "읽기 전용 파일입니다." : ex.Message;
            _dialogs.ShowError("복원 실패", $"{buffer.Path}\n\n{message}");
            _log.Error($"Restore failed: {buffer.Path}", ex);
        }
        finally
        {
            buffer.SaveLock.Release();
        }
    }
}
