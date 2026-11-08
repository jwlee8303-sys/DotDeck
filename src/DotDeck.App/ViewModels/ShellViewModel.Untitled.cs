using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Editors;
using DotDeck.Core.Explorer;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>Opening files from a dialog and saving untitled ("새 파일") documents.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Built-in and user templates (user folder read on each call so new files show up).</summary>
    public IReadOnlyList<DocumentTemplate> LoadTemplates() => TemplateCatalog.Load(_store, Paths.TemplatesDir);

    /// <summary>New untitled document from a template; saving asks for a path with the template's file name.</summary>
    public void NewFromTemplate(DocumentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var doc = new ScratchDocumentViewModel(++_scratchCounter, CloseDocument, template.Text, template.FileName);
        Documents.Add(doc);
        ActiveContent = doc;
    }

    private bool HasUnsavedUntitled => Documents.OfType<ScratchDocumentViewModel>().Any(d => d.IsDirty);

    /// <summary>File → Open (Ctrl+O): opens every chosen file in its own tab.</summary>
    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        var initial = ActiveTextDocument is { } active ? Path.GetDirectoryName(active.FilePath) : Paths.Home;
        foreach (var path in _dialogs.PickOpenFiles("열기", initial))
        {
            await OpenFileAsync(path, preview: false).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Saves an untitled document: the user picks a path, the text moves into a normal editor tab for that file
    /// and is saved through the safe-save pipeline (diff review, backup, atomic write). A new path is first
    /// created as an empty file. Returns false when cancelled or failed; the untitled tab then stays open.
    /// </summary>
    public async Task<bool> SaveUntitledAsync(ScratchDocumentViewModel untitled, string? initialDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(untitled);
        var path = _dialogs.PickSaveFile("다른 이름으로 저장", untitled.SuggestedFileName, initialDirectory ?? Paths.Home);
        if (path is null)
        {
            return false;
        }


        var full = Path.GetFullPath(path);
        if (FindTextDocument(full) is { Buffer.IsDirty: true })
        {
            _dialogs.ShowError("저장할 수 없음", $"이 파일은 이미 열려 있고 저장하지 않은 변경 내용이 있습니다. 먼저 그 탭을 저장하거나 닫으세요.\n\n{path}");
            return false;
        }

        // A new path starts as an empty file so the normal pipeline (diff review, backup, atomic replace) writes
        // the content. If the save does not happen, the empty file is removed again below.
        var created = false;
        try
        {
            if (!_store.FileExists(path))
            {
                _store.CreateFile(path);
                created = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("저장 실패", $"{path}\n\n{ex.Message}");
            return false;
        }

        var wasOpen = FindTextDocument(full) is not null;
        await OpenFileAsync(path, preview: false).ConfigureAwait(true);
        var target = FindTextDocument(full);
        if (target is null || target.Buffer.IsDirty)
        {
            // Not openable (binary, secret link target, read error: the status bar says why) or edited meanwhile.
            CleanUpCreated(created, full, target, wasOpen);
            return false;
        }

        var text = untitled.Text;
        var document = target.Buffer.Document;
        document.Text = text;
        await SaveAsync(target).ConfigureAwait(true);

        // Only a save that wrote exactly this text retires the untitled tab (cancelled review, conflict reload or
        // failure keep it, so nothing typed is lost).
        if (!target.Buffer.IsDirty && document.Text == text)
        {
            if (untitled.Text != text)
            {
                // Typed into while saving: keep the untitled tab with the newer text.
                return false;
            }

            Documents.Remove(untitled);
            ActiveContent = target;
            return true;
        }

        if (target.Buffer.IsDirty && document.Text == text && document.UndoStack.CanUndo)
        {
            document.UndoStack.Undo();
        }

        CleanUpCreated(created, full, target, wasOpen);
        ActiveContent = untitled;
        return false;
    }

    private bool CanSaveActiveAs() => ActiveContent is ScratchDocumentViewModel || ActiveTextDocument is not null;

    /// <summary>
    /// Saves the active tab's text to a new file (same pipeline as a new file: diff review, atomic write). The tab
    /// then shows the new file; the original file is left as it was on disk (its unsaved edits went to the copy).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveActiveAs))]
    private async Task SaveActiveAsAsync()
    {
        switch (ActiveContent)
        {
            case ScratchDocumentViewModel untitled:
                await SaveUntitledAsync(untitled);
                break;
            case TextDocumentViewModel text:
                var copy = new ScratchDocumentViewModel(0, _ => { }, text.Buffer.Document.Text, Path.GetFileName(text.FilePath));
                if (await SaveUntitledAsync(copy, Path.GetDirectoryName(text.FilePath)))
                {
                    var saved = ActiveContent;
                    if (!string.Equals(Path.GetFullPath(text.FilePath), (saved as TextDocumentViewModel)?.FilePath is { } p ? Path.GetFullPath(p) : null, StringComparison.OrdinalIgnoreCase))
                    {
                        RemoveTextDocument(text);
                    }

                    ActiveContent = saved;
                }
                else if (Documents.Contains(text))
                {
                    ActiveContent = text; // cancelled or failed: back to the original tab, not the temporary copy
                }

                break;
        }
    }

    private TextDocumentViewModel? FindTextDocument(string fullPath) =>
        Documents.OfType<TextDocumentViewModel>()
            .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the empty file this save created (and its tab) when nothing was written to it.</summary>
    private void CleanUpCreated(bool created, string path, TextDocumentViewModel? target, bool wasOpen)
    {
        // Never delete a file whose tab holds edits (they would have nowhere to be saved).
        if (!created || target is { Buffer.IsDirty: true })
        {
            return;
        }

        try
        {
            if (_store.GetEntry(path) is { IsDirectory: false, Length: 0 })
            {
                if (target is not null && !wasOpen)
                {
                    RemoveTextDocument(target);
                }

                _store.DeleteFile(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Could not remove empty new file: {path} ({ex.GetType().Name})");
        }
    }

    /// <summary>Save all: untitled tabs first (each asks for a path). False when one was cancelled.</summary>
    private async Task<bool> SaveAllUntitledAsync()
    {
        foreach (var untitled in Documents.OfType<ScratchDocumentViewModel>().Where(d => d.IsDirty).ToList())
        {
            ActiveContent = untitled;
            if (!await SaveUntitledAsync(untitled).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    private async Task CloseUntitledAsync(ScratchDocumentViewModel untitled)
    {
        if (untitled.IsDirty)
        {
            switch (_dialogs.AskSaveChanges("DotDeck", $"{untitled.Title.TrimEnd(' ', '*')}\n\n변경 내용을 저장할까요?"))
            {
                case SaveChangesChoice.Cancel:
                    return;
                case SaveChangesChoice.Save:
                    await SaveUntitledAsync(untitled).ConfigureAwait(true);
                    return;
            }
        }

        Documents.Remove(untitled);
    }

    /// <summary>Exit check for untitled documents (before the file buffers). False cancels closing.</summary>
    private async Task<bool> ConfirmCloseUntitledAsync()
    {
        foreach (var untitled in Documents.OfType<ScratchDocumentViewModel>().Where(d => d.IsDirty).ToList())
        {
            ActiveContent = untitled;
            switch (_dialogs.AskSaveChanges("DotDeck", $"{untitled.Title.TrimEnd(' ', '*')}\n\n변경 내용을 저장할까요?"))
            {
                case SaveChangesChoice.Cancel:
                    return false;
                case SaveChangesChoice.Discard:
                    Documents.Remove(untitled);
                    break;
                case SaveChangesChoice.Save when !await SaveUntitledAsync(untitled).ConfigureAwait(true):
                    return false;
            }
        }

        return true;
    }
}
