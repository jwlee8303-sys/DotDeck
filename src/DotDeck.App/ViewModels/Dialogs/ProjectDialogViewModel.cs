using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Projects;

namespace DotDeck.App.ViewModels.Dialogs;

/// <summary>Values entered in the project dialog.</summary>
public sealed record ProjectDialogResult(string Name, string Root, string? Color, ExplorerFilterMode Filter);

/// <summary>A selectable accent color (null = none).</summary>
public sealed record ColorChoice(string? Hex, string Label);

/// <summary>
/// "프로젝트 추가" / "프로젝트 편집" (SPEC 2.2: name + folder; optional color and filter).
/// The commit callback saves through the registry and returns an error message to show, or null on success;
/// the dialog stays open on error so the user can correct the input.
/// </summary>
public sealed partial class ProjectDialogViewModel : ObservableObject
{
    private readonly Func<ProjectDialogResult, Task<string?>> _commit;
    private readonly Func<string?, string?> _pickFolder;
    private readonly Func<string, bool> _directoryExists;
    private bool _nameEditedByUser;

    public ProjectDialogViewModel(
        string title,
        ProjectDefinition? existing,
        string? builtinRoot,
        Func<ProjectDialogResult, Task<string?>> commit,
        Func<string?, string?> pickFolder,
        Func<string, bool> directoryExists)
    {
        Title = title;
        _commit = commit;
        _pickFolder = pickFolder;
        _directoryExists = directoryExists;
        IsBuiltin = existing?.IsBuiltin == true;
        _name = existing?.Name ?? string.Empty;
        _root = existing?.Root ?? builtinRoot ?? string.Empty;
        _nameEditedByUser = existing is not null;
        _isConfigDocs = existing is null || existing.Filter == ExplorerFilterMode.ConfigDocs;
        _selectedColor = Colors.FirstOrDefault(c => string.Equals(c.Hex, existing?.Color, StringComparison.OrdinalIgnoreCase))
            ?? (existing?.Color is { } custom ? new ColorChoice(custom, custom) : Colors[0]);
    }

    /// <summary>Raised with true after a successful commit, false on cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    /// <summary>Built-in projects keep their fixed name and root.</summary>
    public bool IsBuiltin { get; }

    public bool CanEditNameAndRoot => !IsBuiltin;

    public IReadOnlyList<ColorChoice> Colors { get; } =
    [
        new(null, "없음"),
        new("#68217A", "보라"),
        new("#0078D4", "파랑"),
        new("#038387", "청록"),
        new("#16825D", "초록"),
        new("#CCA700", "노랑"),
        new("#CA5100", "주황"),
        new("#C50F1F", "빨강"),
        new("#B4009E", "자홍"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _root;

    /// <summary>Null when the list selection was cleared (Ctrl+click); treated as no color.</summary>
    [ObservableProperty]
    private ColorChoice? _selectedColor;

    [ObservableProperty]
    private bool _isConfigDocs;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private bool _isBusy;

    partial void OnNameChanged(string value)
    {
        if (!_suppressNameTracking)
        {
            _nameEditedByUser = value.Length > 0;
        }
    }

    private bool _suppressNameTracking;

    [RelayCommand]
    private void Browse()
    {
        var picked = _pickFolder(string.IsNullOrWhiteSpace(Root) ? null : Root);
        if (picked is null)
        {
            return;
        }

        Root = picked;

        // Suggest the folder name until the user types a name of their own.
        if (!_nameEditedByUser)
        {
            _suppressNameTracking = true;
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(picked)) is { Length: > 0 } folderName ? folderName : picked;
            _suppressNameTracking = false;
        }
    }

    private bool CanOk() => !IsBusy && (IsBuiltin || (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Root)));

    [RelayCommand(CanExecute = nameof(CanOk))]
    private async Task OkAsync()
    {
        Error = null;
        var root = Root.Trim().Trim('"');
        if (!IsBuiltin && !Path.IsPathFullyQualified(root))
        {
            Error = "폴더는 전체 경로(예: D:\\project\\app)로 입력하세요.";
            return;
        }

        // Capture the input before going async so later edits cannot mix into this commit.
        var filter = IsConfigDocs ? ExplorerFilterMode.ConfigDocs : ExplorerFilterMode.All;
        var result = new ProjectDialogResult(Name.Trim(), root, SelectedColor?.Hex, filter);
        IsBusy = true;
        try
        {
            // A slow network path must not freeze the dialog.
            if (!IsBuiltin && !await Task.Run(() => _directoryExists(root)))
            {
                Error = $"폴더가 없습니다: {root}";
                return;
            }

            Error = await _commit(result);
        }
        finally
        {
            IsBusy = false;
        }

        if (Error is null)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsBusy)
        {
            CloseRequested?.Invoke(this, false);
        }
    }
}
