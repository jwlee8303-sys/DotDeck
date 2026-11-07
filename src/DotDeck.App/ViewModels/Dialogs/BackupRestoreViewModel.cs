using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Documents;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>One backup of the file, as listed in the restore dialog.</summary>
public sealed record BackupEntry(BackupSet Set, BackupFile File)
{
    public string When => Set.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string Reason => Set.Reason switch
    {
        "save" => "저장 전",
        "before-restore" => "복원 전",
        _ => Set.Reason,
    };

    public string Size => File.Length < 1024 ? $"{File.Length} B" : $"{File.Length / 1024.0:0.0} KB";
}

/// <summary>
/// Lists the backups of one file and previews the selected one against the current editor text
/// ("-" current, "+" backup). <see cref="Chosen"/> is set when the user confirms.
/// </summary>
public sealed partial class BackupRestoreViewModel : ObservableObject
{
    private readonly Func<BackupEntry, string> _loadText;
    private readonly string _currentText;

    /// <param name="loadText">Reads and decodes a backup; throws on damaged/binary content.</param>
    public BackupRestoreViewModel(string path, IReadOnlyList<BackupEntry> entries, string currentText, Func<BackupEntry, string> loadText)
    {
        Path = path;
        Entries = entries;
        _currentText = currentText;
        _loadText = loadText;
        SelectedEntry = entries.FirstOrDefault();
    }

    public string Path { get; }

    public string Title => $"백업에서 복원 — {System.IO.Path.GetFileName(Path)}";

    public IReadOnlyList<BackupEntry> Entries { get; }

    public bool HasEntries => Entries.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private BackupEntry? _selectedEntry;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> _lines = [];

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isSelectionValid;

    public BackupEntry? Chosen { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    partial void OnSelectedEntryChanged(BackupEntry? value)
    {
        Lines = [];
        IsSelectionValid = false;
        if (value is null)
        {
            Message = HasEntries ? null : "이 파일의 백업이 없습니다.";
            return;
        }

        try
        {
            var diff = TextDiffService.Compute(_currentText, _loadText(value));
            Lines = diff.Compact();
            Message = diff.HasChanges ? $"현재 내용 대비 +{diff.InsertedCount} −{diff.DeletedCount}" : "현재 내용과 같습니다.";
            IsSelectionValid = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"백업을 읽을 수 없습니다: {ex.Message}";
        }
    }

    private bool CanRestore() => SelectedEntry is not null && IsSelectionValid;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        Chosen = SelectedEntry;
        CloseRequested?.Invoke(this, true);
    }
}
