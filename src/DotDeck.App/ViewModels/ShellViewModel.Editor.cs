using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Documents;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>Editor tabs: open (preview / normal), save, extra views, close with unsaved-change prompts.</summary>
public sealed partial class ShellViewModel : ITextDocumentHost
{
    private readonly Dictionary<string, int> _viewCounters = new(StringComparer.OrdinalIgnoreCase);
    private int _openRequest;

    /// <summary>The editor tab the status bar describes: the active text tab, kept while a tool pane has focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTextDocument))]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveCommand), nameof(FindCommand), nameof(ReplaceCommand))]
    private TextDocumentViewModel? _activeTextDocument;

    [ObservableProperty]
    private string? _lastBackupText;

    public bool HasActiveTextDocument => ActiveTextDocument is not null;

    public bool HasUnsavedDocuments => _buffers.Buffers.Any(b => b.IsDirty);

    private TextDocumentViewModel? PreviewDocument => Documents.OfType<TextDocumentViewModel>().FirstOrDefault(d => d.IsPreview);

    partial void OnActiveContentChanged(PaneViewModel? value)
    {
        switch (value)
        {
            case TextDocumentViewModel text:
                ActiveTextDocument = text;
                break;
            case DocumentViewModel:
                ActiveTextDocument = null;
                break;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> in an editor tab. A preview open reuses the single preview tab and never asks
    /// questions (large files need an explicit open); binary files are only described.
    /// </summary>
    public async Task OpenFileAsync(string path, bool preview)
    {
        // Every open supersedes earlier pending previews: a slow preview must not replace a newer one.
        var request = ++_openRequest;
        if (ActivateExisting(path, preview))
        {
            return;
        }

        try
        {
            var probe = await Task.Run(() => _buffers.Reader.Probe(path)).ConfigureAwait(true);
            if (probe.IsBinary)
            {
                StatusMessage = $"바이너리 파일이라 열지 않습니다: {System.IO.Path.GetFileName(path)} ({FormatSize(probe.Length)})";
                return;
            }

            if (probe.IsLarge)
            {
                if (preview)
                {
                    StatusMessage = $"큰 파일({FormatSize(probe.Length)})은 미리 보지 않습니다. 더블클릭하면 엽니다.";
                    return;
                }

                if (!_dialogs.Confirm("큰 파일 열기", $"{System.IO.Path.GetFileName(path)}의 크기는 {FormatSize(probe.Length)}입니다. 편집기가 느려질 수 있습니다.\n\n그래도 열까요?"))
                {
                    return;
                }
            }

            if ((preview && request != _openRequest) || ActivateExisting(path, preview))
            {
                return;
            }

            var buffer = await _buffers.AcquireAsync(path).ConfigureAwait(true);

            // Re-check after the read: a concurrent open may have created the tab, or a newer preview won.
            if ((preview && request != _openRequest) || ActivateExisting(path, preview))
            {
                _buffers.Release(buffer);
                return;
            }

            if (preview && PreviewDocument is { } oldPreview && !oldPreview.Buffer.IsDirty)
            {
                RemoveTextDocument(oldPreview);
            }

            var document = CreateTextDocument(buffer, preview);
            if (buffer.Snapshot.HasDecodingErrors)
            {
                StatusMessage = $"일부 바이트를 해석하지 못했습니다({buffer.EncodingText}). 저장하려면 인코딩을 지정해야 합니다.";
            }
            else if (buffer.Snapshot.LineEndings.IsMixed)
            {
                StatusMessage = $"줄바꿈이 섞여 있습니다: {System.IO.Path.GetFileName(path)}";
            }

            ActiveContent = document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = ex switch
            {
                SecretFileException => "비밀 파일은 열 수 없습니다.",
                BinaryFileException => "바이너리 파일이라 열지 않습니다.",
                FileNotFoundException => "파일이 없습니다.",
                _ => ex.Message,
            };
            StatusMessage = $"{message} {path}";
            _log.Warning($"Open failed: {path} ({ex.GetType().Name})");
        }
    }

    private bool ActivateExisting(string path, bool preview)
    {
        var existing = Documents.OfType<TextDocumentViewModel>()
            .FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        if (!preview)
        {
            existing.Promote();
        }

        ActiveContent = existing;
        return true;
    }

    /// <summary>Saves the buffer behind <paramref name="document"/> through the safe-write pipeline.</summary>
    public async Task SaveAsync(TextDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        await SaveBufferAsync(document.Buffer).ConfigureAwait(true);
    }

    public void OpenNewView(TextDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Promote();
        _buffers.AddReference(document.Buffer);
        ActiveContent = CreateTextDocument(document.Buffer, preview: false);
    }

    /// <summary>Asks about every unsaved buffer before the app exits. False cancels closing.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        // Re-evaluated after every answer: edits made while a save runs (even to a buffer the user chose to
        // discard) must be asked about again. A "discard" holds only for the change count it was given for.
        var discarded = new Dictionary<DocumentBuffer, long>();
        while (true)
        {
            await WaitForPendingSavesAsync().ConfigureAwait(true);
            var buffer = _buffers.Buffers.FirstOrDefault(b =>
                b.IsDirty && !(discarded.TryGetValue(b, out var count) && count == b.ChangeCount));
            if (buffer is null)
            {
                return true;
            }

            switch (_dialogs.AskSaveChanges("DotDeck", $"{buffer.Path}\n\n변경 내용을 저장할까요?"))
            {
                case SaveChangesChoice.Cancel:
                    return false;
                case SaveChangesChoice.Discard:
                    discarded[buffer] = buffer.ChangeCount;
                    break;
                case SaveChangesChoice.Save when !await SaveBufferAsync(buffer).ConfigureAwait(true):
                    return false;
            }
        }
    }

    private async Task WaitForPendingSavesAsync()
    {
        foreach (var buffer in _buffers.Buffers.ToList())
        {
            await buffer.SaveLock.WaitAsync().ConfigureAwait(true);
            buffer.SaveLock.Release();
        }
    }

    private async Task<bool> SaveBufferAsync(DocumentBuffer buffer, bool preview = true, WritePlan? approved = null)
    {
        // One save per buffer at a time; a queued save plans from the text and snapshot current when it starts
        // (unless an already reviewed plan is given).
        await buffer.SaveLock.WaitAsync().ConfigureAwait(true);
        try
        {
            return await SaveBufferCoreAsync(buffer, preview, approved).ConfigureAwait(true);
        }
        finally
        {
            buffer.SaveLock.Release();
        }
    }

    private static string DescribeBlocked(WritePlan plan)
    {
        var detail = plan.UnencodableCharacters.Count > 0
            ? "\n\n" + string.Join("\n", plan.UnencodableCharacters.Take(5).Select(c => $"줄 {c.Line}, 열 {c.Column}: {c.Text}"))
            : string.Empty;
        return $"{plan.BlockingReason}{detail}";
    }

    private static DiffReviewItem ToReviewItem(WritePlan plan) =>
        new(plan.Path, plan.Diff, plan.LineEndingsChanged ? "줄바꿈 변경" : null);

    private async Task<bool> SaveBufferCoreAsync(DocumentBuffer buffer, bool preview, WritePlan? approved)
    {
        var text = approved?.NewText ?? buffer.Document.Text;

        // Change count matching `text`; -1 when the editor already differs from an approved plan's text,
        // so a later "reload" never discards edits the user has not seen in the comparison.
        var textChangeCount = string.Equals(buffer.Document.Text, text, StringComparison.Ordinal) ? buffer.ChangeCount : -1;
        var baseline = buffer.Snapshot;
        var plan = approved ?? await Task.Run(() => SafeWriter.Prepare(buffer.Path, baseline, text)).ConfigureAwait(true);
        if (!plan.CanCommit)
        {
            _dialogs.ShowError("저장할 수 없음", $"{buffer.Path}\n\n{DescribeBlocked(plan)}");
            return false;
        }

        // SPEC 6: every write is previewed as a diff first (can be skipped for the rest of the session).
        if (approved is null && preview && !_skipSavePreview && !plan.IsUnchanged)
        {
            var review = new DiffReviewViewModel(
                "저장 전 확인",
                $"{buffer.Path}\n저장하면 현재 디스크 내용은 백업됩니다.",
                [ToReviewItem(plan)],
                [new DialogChoice("save", "저장", IsDefault: true), new DialogChoice("cancel", "취소", IsCancel: true)])
            {
                SkipLabel = "이번 실행 동안 저장 전 미리보기 생략",
            };
            if (_dialogs.ShowDiffReview(review) != "save")
            {
                StatusMessage = "저장을 취소했습니다.";
                return false;
            }

            _skipSavePreview |= review.SkipNextTime;
        }

        var overwrite = false;
        while (true)
        {
            try
            {
                var result = await Task.Run(() => _writer.Commit(plan, overwrite)).ConfigureAwait(true);
                buffer.MarkSaved(result.Snapshot, text);
                buffer.DismissedDiskHash = null;
                if (result.Backup is { } backup)
                {
                    LastBackupText = $"백업: {backup.Directory}";
                }

                StatusMessage = result.Written ? $"저장했습니다: {buffer.Path}" : $"변경 사항 없음: {buffer.FileName}";
                _log.Info($"Saved {buffer.Path}{(result.Backup is { } b ? $" (backup {b.Id})" : string.Empty)}");
                return true;
            }
            catch (ExternalChangeException ex) when (!overwrite)
            {
                switch (await ResolveConflictAsync(buffer, ex.Kind, text, onSave: true).ConfigureAwait(true))
                {
                    case "overwrite":
                        overwrite = true;
                        break;
                    case "reload":
                        await ReloadAsync(buffer, textChangeCount).ConfigureAwait(true);
                        return false;
                    default:
                        StatusMessage = "저장을 취소했습니다.";
                        return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = ex is ReadOnlyFileException ? "읽기 전용 파일입니다." : ex.Message;
                _dialogs.ShowError("저장 실패", $"{buffer.Path}\n\n{message}");
                _log.Error($"Save failed: {buffer.Path}", ex);
                return false;
            }
        }
    }

    private TextDocumentViewModel CreateTextDocument(DocumentBuffer buffer, bool preview)
    {
        var number = _viewCounters.GetValueOrDefault(buffer.Path) + 1;
        _viewCounters[buffer.Path] = number;
        var document = new TextDocumentViewModel(buffer, number, preview, this, CloseDocument);
        Documents.Add(document);
        UpdateContentWatchers();
        return document;
    }

    /// <summary>Close request from a tab. False when the user cancelled.</summary>
    private bool TryCloseTextDocument(TextDocumentViewModel document)
    {
        var lastView = !Documents.OfType<TextDocumentViewModel>().Any(d => d != document && d.Buffer == document.Buffer);
        if (lastView && document.Buffer.SaveLock.CurrentCount == 0)
        {
            // Closing now could drop edits the running save does not include (or keep undone ones on disk).
            StatusMessage = $"저장 중입니다. 저장이 끝난 뒤 닫으세요: {document.Buffer.FileName}";
            return false;
        }

        if (lastView && document.Buffer.IsDirty)
        {
            switch (_dialogs.AskSaveChanges("DotDeck", $"{document.FilePath}\n\n변경 내용을 저장할까요?"))
            {
                case SaveChangesChoice.Cancel:
                    return false;
                case SaveChangesChoice.Save:
                    _ = SaveThenCloseAsync(document);
                    return false;
            }
        }

        RemoveTextDocument(document);
        return true;
    }

    private async Task SaveThenCloseAsync(TextDocumentViewModel document)
    {
        if (await SaveBufferAsync(document.Buffer).ConfigureAwait(true) && !document.Buffer.IsDirty)
        {
            RemoveTextDocument(document);
        }
    }

    private void RemoveTextDocument(TextDocumentViewModel document)
    {
        if (!Documents.Remove(document))
        {
            return;
        }

        document.Detach();
        _buffers.Release(document.Buffer);
        if (_buffers.Find(document.FilePath) is null)
        {
            _viewCounters.Remove(document.FilePath);
            UpdateContentWatchers();
        }

        if (ActiveTextDocument == document)
        {
            ActiveTextDocument = null;
        }
    }

    private bool CanUseActiveTextDocument() => ActiveTextDocument is not null;

    [RelayCommand(CanExecute = nameof(CanUseActiveTextDocument))]
    private Task SaveActiveAsync() => ActiveTextDocument is { } doc ? SaveAsync(doc) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUseActiveTextDocument))]
    private void Find() => ActiveTextDocument?.Find.Open(replace: false);

    [RelayCommand(CanExecute = nameof(CanUseActiveTextDocument))]
    private void Replace() => ActiveTextDocument?.Find.Open(replace: true);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
