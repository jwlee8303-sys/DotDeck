using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Toml;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>Initial structural TOML form. Schema-specific controls are added separately.</summary>
public sealed partial class TomlFormViewModel(Action<string> apply) : DocumentFormViewModel(apply)
{
    public sealed record Row(IReadOnlyList<string> Path, string Label, string Literal, bool CanDelete);
    private TomlEditDocument? _document;
    public ObservableCollection<Row> Rows { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyValueCommand), nameof(DeleteKeyCommand))]
    private Row? _selected;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddKeyCommand), nameof(CreateTableCommand))]
    private string _path = string.Empty;
    [ObservableProperty]
    private string _literal = string.Empty;
    [ObservableProperty]
    private string? _message;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyValueCommand), nameof(DeleteKeyCommand), nameof(AddKeyCommand), nameof(CreateTableCommand), nameof(CleanupCommand))]
    private bool _canEdit;

    protected override string? LoadCore(string text)
    {
        try
        {
            _document = TomlEditDocument.Parse(Encoding.UTF8.GetBytes(text));
            // Aggregate values may contain nested secrets, so mask the entire document until protected backups exist.
            CanEdit = true;
            Message = CanEdit ? "TOML 구조 폼입니다. Codex 스키마·설정 계층 안내는 후속 단계에서 제공됩니다."
                : "비밀값 가능성이 있어 값을 가렸습니다. 암호화 백업 지원 전에는 이 폼에서 편집할 수 없습니다.";
            RefreshRows();
            return null;
        }
        catch (TomlEditException ex)
        {
            _document = null;
            CanEdit = false;
            Rows.Clear();
            return $"TOML 오류: {ex.Line}행 {ex.Column}열";
        }
    }

    private static bool IsSensitive(string segment) => new[] { "KEY", "TOKEN", "SECRET", "PASSWORD", "PWD", "CREDENTIAL", "ENV", "HTTP_HEADERS", "OAUTH" }
        .Any(word => segment.Contains(word, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedChanged(Row? value)
    {
        Literal = value?.Literal ?? string.Empty;
    }

    private bool CanApply() => CanEdit && Selected is not null;
    private bool CanDelete() => CanApply() && Selected?.CanDelete == true;
    private bool CanCreate() => CanEdit && TryPath(out _);
    private bool CanAdd() => CanEdit && TryPath(out var path) && _document?.CanSetValue(path) == true;

    private bool TryPath(out IReadOnlyList<string> path)
    {
        try { path = TomlEditDocument.ParsePath(Path); return true; }
        catch (TomlEditException) { path = []; return false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyValue() { if (CanApply()) Edit(d => d.SetValue(Selected!.Path, Literal)); }
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteKey() { if (CanDelete()) Edit(d => d.DeleteKey(Selected!.Path)); }
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddKey() { if (CanAdd() && TryPath(out var path)) Edit(d => d.SetValue(path, Literal)); }
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateTable() { if (CanCreate() && TryPath(out var path)) Edit(d => d.CreateTable(path)); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Cleanup() { if (CanEdit) Edit(d => d.RemoveEmptyTables()); }

    private void Edit(Action<TomlEditDocument> edit)
    {
        if (_document is null) return;
        try
        {
            // Apply is synchronous and may refresh the owning form, so publish only complete operations.
            var candidate = TomlEditDocument.Parse(_document.ToBytes());
            edit(candidate);
            _document = candidate;
            RefreshRows();
            Apply(Encoding.UTF8.GetString(candidate.ToBytes()));
        }
        catch (Exception ex) when (ex is TomlEditException or ArgumentException)
        {
            Message = "변경을 적용하지 못했습니다. 키 경로·값 형식·지원 범위를 확인하세요.";
        }
    }

    private void RefreshRows()
    {
        Selected = null;
        Rows.Clear();
        foreach (var entry in _document?.Entries ?? [])
            Rows.Add(new Row(entry.Path, TomlEditDocument.FormatPath(entry.Path), CanEdit ? entry.Literal : "••••", entry.CanDelete));
        AddKeyCommand.NotifyCanExecuteChanged();
        CreateTableCommand.NotifyCanExecuteChanged();
    }
}
