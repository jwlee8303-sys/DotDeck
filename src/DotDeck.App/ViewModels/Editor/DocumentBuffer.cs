using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotDeck.Core.Documents;
using ICSharpCode.AvalonEdit.Document;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>
/// The text of one file shared by every tab showing it ("new window" opens a second view on the same buffer),
/// so undo history and dirty state are shared. Owned by <see cref="DocumentBufferManager"/>; UI thread only.
/// </summary>
public sealed partial class DocumentBuffer : ObservableObject
{
    internal DocumentBuffer(TextFileSnapshot snapshot)
    {
        _snapshot = snapshot;
        IsSecretFile = snapshot.IsSecretFile;
        Path = snapshot.Path;
        Document = new TextDocument(snapshot.Text) { FileName = snapshot.Path };
        Document.UndoStack.PropertyChanged += OnUndoStackChanged;
        Document.TextChanged += (_, _) =>
        {
            ChangeCount++;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Path { get; }

    /// <summary>The file, or the final target of its links, is a secret file. Only grows (saved snapshots lack it).</summary>
    public bool IsSecretFile { get; private set; }

    partial void OnSnapshotChanged(TextFileSnapshot value) => IsSecretFile |= value.IsSecretFile;

    public string FileName => System.IO.Path.GetFileName(Path);

    public TextDocument Document { get; }

    /// <summary>What is on disk as far as DotDeck knows: the baseline for diffs and external change checks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncodingText), nameof(LineEndingText))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(ReadOnlyReason))]
    private TextFileSnapshot _snapshot;

    public bool IsReadOnly => Snapshot.IsReadOnly;
    public string? ReadOnlyReason => Snapshot.ReadOnlyReason;

    [ObservableProperty]
    private bool _isDirty;

    // ===== Soft wrap: one state per file, so every view of the buffer follows it =====

    /// <summary>Soft wrap from the per-extension default (<see cref="Core.Settings.WordWrapDefaults"/>), set on open.</summary>
    public bool DefaultWordWrap { get; internal set; }

    /// <summary>
    /// What the user chose with the toggle (saved in the session); null follows <see cref="DefaultWordWrap"/>.
    /// Display only: the document, undo stack and dirty state never see it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWordWrapEnabled))]
    private bool? _wordWrapChoice;

    public bool IsWordWrapEnabled => WordWrapChoice ?? DefaultWordWrap;

    internal int ReferenceCount { get; set; }

    /// <summary>Increases on every text change (including undo/redo).</summary>
    public long ChangeCount { get; private set; }

    public string EncodingText => Snapshot.EncodingDisplayName + (Snapshot.HasDecodingErrors ? " (디코딩 오류)" : string.Empty);

    public string LineEndingText
    {
        get
        {
            var info = Snapshot.LineEndings;
            var name = info.Dominant switch
            {
                LineEnding.Lf => "LF",
                LineEnding.Cr => "CR",
                _ => "CRLF",
            };
            return info.IsMixed ? $"{name} (혼합)" : name;
        }
    }

    public event EventHandler? TextChanged;

    /// <summary>
    /// Records a successful save. The buffer becomes clean only if the editor still holds the saved text
    /// (the user may have typed while the write ran in the background).
    /// </summary>
    public void MarkSaved(TextFileSnapshot saved, string savedText)
    {
        Snapshot = saved;
        if (string.Equals(Document.Text, savedText, StringComparison.Ordinal))
        {
            Document.UndoStack.MarkAsOriginalFile();
        }
        else
        {
            // The text changed during the save (typing or undo). The undo stack's "original" point no longer
            // matches the disk, so drop it: the buffer stays dirty until the next save.
            Document.UndoStack.DiscardOriginalFileMarker();
            IsDirty = true;
        }
    }

    /// <summary>
    /// Replaces the text with what is on disk. The replacement is one undoable edit, so a mistaken reload can be
    /// undone (the buffer then becomes dirty again).
    /// </summary>
    public void Reload(TextFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(Document.Text, snapshot.Text, StringComparison.Ordinal))
        {
            Document.Text = snapshot.Text;
        }

        Snapshot = snapshot;
        DismissedDiskHash = null;
        Document.UndoStack.MarkAsOriginalFile();
    }

    /// <summary>The disk now holds <paramref name="snapshot"/> but the editor text differs: keep the text, stay dirty.</summary>
    public void AcceptDiskSnapshot(TextFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        DismissedDiskHash = null;
        Document.UndoStack.DiscardOriginalFileMarker();
        IsDirty = true;
    }

    /// <summary>Disk content hash the user chose to keep their edits over ("deleted" for a deleted file).</summary>
    internal string? DismissedDiskHash { get; set; }

    /// <summary>Serializes saves of this buffer.</summary>
    internal SemaphoreSlim SaveLock { get; } = new(1, 1);

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
        {
            IsDirty = !Document.UndoStack.IsOriginalFile;
        }
    }
}
