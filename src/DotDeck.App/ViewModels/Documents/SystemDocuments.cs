using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.Documents;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Security;
using DotDeck.Core.SystemConfig;

namespace DotDeck.App.ViewModels.Documents;

/// <summary>Everything the system documents need (SPEC §8).</summary>
public sealed record SystemContext(
    IEnvironmentStore Environment,
    IFileStore Store,
    AppPaths Paths,
    IDialogService Dialogs,
    Action<string> SetStatus,
    Action RestartAsAdministrator,
    Func<string> BackupsDirectory);

public sealed partial class EnvironmentRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue))]
    private string _value = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue))]
    private bool _isRevealed;

    [ObservableProperty]
    private string? _issue;

    public bool IsSecret => SecretMasker.IsSecretName(Name);

    public bool IsMasked => IsSecret && !IsRevealed;

    public string DisplayValue => IsMasked ? SecretMasker.Mask : Value;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsSecret));
        OnPropertyChanged(nameof(IsMasked));
        OnPropertyChanged(nameof(DisplayValue));
    }

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(IsMasked));
}

/// <summary>Shared base: load, dirty tracking, diff review then apply with a masked history record.</summary>
public abstract partial class SystemDocumentViewModel : DocumentViewModel
{
    protected SystemDocumentViewModel(string contentId, string title, SystemContext context, Action<DocumentViewModel> close)
        : base(contentId, title, close)
    {
        Context = context;
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (EnvironmentRow row in e.NewItems ?? Array.Empty<EnvironmentRow>())
            {
                row.PropertyChanged += OnRowChanged;
            }

            OnEdited();
        };
    }

    protected SystemContext Context { get; }

    public override bool IsRestorable => true;

    public ObservableCollection<EnvironmentRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite), nameof(IsReadOnly), nameof(ScopeText))]
    private EnvironmentScope _scope;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _filter = string.Empty;

    public bool CanWrite => Context.Environment.CanWrite(Scope);

    public bool IsReadOnly => !CanWrite;

    public string ScopeText => Scope == EnvironmentScope.User ? "사용자" : "시스템";

    public IReadOnlyList<EnvironmentScope> Scopes { get; } = [EnvironmentScope.User, EnvironmentScope.Machine];

    protected bool Loading { get; private set; }

    // Values as read by the last Reload: edits are diffed against these, and a value changed elsewhere since
    // then is a conflict (never silently overwritten).
    private IReadOnlyDictionary<string, string> _baseline = new Dictionary<string, string>();

    public void Reload()
    {
        Loading = true;
        try
        {
            foreach (var row in Rows)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Rows.Clear();
            _baseline = Context.Environment.Read(Scope);
            foreach (var row in LoadRows(_baseline))
            {
                Rows.Add(row);
            }
        }
        finally
        {
            Loading = false;
        }

        IsDirty = false;
        AfterEdit();
    }

    protected abstract IEnumerable<EnvironmentRow> LoadRows(IReadOnlyDictionary<string, string> values);

    /// <summary>The variables of the scope after this document's edits.</summary>
    protected abstract IReadOnlyList<KeyValuePair<string, string>> Target(IReadOnlyDictionary<string, string> current);

    protected virtual void AfterEdit()
    {
    }

    /// <summary>A reason the edits cannot be applied yet (e.g. duplicate names), or null.</summary>
    protected virtual string? BlockingError() => null;

    private bool _revertingScope;

    partial void OnScopeChanged(EnvironmentScope oldValue, EnvironmentScope newValue)
    {
        if (_revertingScope)
        {
            return;
        }

        if (IsDirty && !Context.Dialogs.Confirm("범위 바꾸기", "적용하지 않은 변경이 있습니다. 버리고 범위를 바꿀까요?"))
        {
            _revertingScope = true;
            try
            {
                Scope = oldValue;
            }
            finally
            {
                _revertingScope = false;
            }

            return;
        }

        Reload();
    }

    [RelayCommand]
    private void Remove(EnvironmentRow? row)
    {
        if (row is not null && CanWrite)
        {
            Rows.Remove(row);
        }
    }

    [RelayCommand]
    private void Revert() => Reload();

    [RelayCommand]
    private void Apply()
    {
        if (!CanWrite)
        {
            return;
        }

        if (BlockingError() is { } blocking)
        {
            Context.Dialogs.ShowError("환경변수", blocking);
            return;
        }

        var changes = EnvironmentChanges.Compute(_baseline, Target(_baseline));
        if (changes.Count == 0)
        {
            Context.SetStatus("변경 사항이 없습니다.");
            IsDirty = false;
            return;
        }

        if (Review($"{ScopeText} 환경변수 변경", $"{changes.Count}개 변수를 바꿉니다. 이미 열린 터미널·프로그램에는 적용되지 않습니다.", changes))
        {
            Write(changes, $"환경변수 {changes.Count}개를 바꿨습니다. 새로 연 터미널부터 적용됩니다.");
        }
    }

    private bool Review(string title, string message, IReadOnlyList<EnvironmentChange> changes)
    {
        var (before, after) = EnvironmentChanges.PreviewText(changes);
        var review = new DiffReviewViewModel(
            title,
            message,
            [new DiffReviewItem($"{ScopeText} 환경변수", TextDiffService.Compute(before, after))],
            [new DialogChoice("apply", "적용", IsDefault: true), new DialogChoice("cancel", "취소", IsCancel: true)]);
        return Context.Dialogs.ShowDiffReview(review) == "apply";
    }

    // Writes after re-reading the scope: values changed elsewhere since they were read abort the write.
    private void Write(IReadOnlyList<EnvironmentChange> changes, string done)
    {
        try
        {
            var conflicts = EnvironmentChanges.Conflicts(Context.Environment.Read(Scope), changes);
            if (conflicts.Count > 0)
            {
                Context.Dialogs.ShowError("환경변수", $"다른 곳에서 바뀐 변수가 있어 적용하지 않았습니다: {string.Join(", ", conflicts)}\n\n'변경 취소'로 다시 불러온 뒤 편집하세요.");
                return;
            }

            EnvironmentChanges.Apply(Context.Environment, Scope, changes, Context.Store, Context.BackupsDirectory(), TimeProvider.System);
            Context.SetStatus(done);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Context.Dialogs.ShowError("환경변수", $"적용하지 못했습니다(이미 바뀐 항목은 변경 기록에 남아 되돌릴 수 있습니다).\n\n{ex.Message}");
        }

        Reload();
    }

    [RelayCommand]
    private void Undo()
    {
        if (!CanWrite)
        {
            return;
        }

        var record = EnvironmentChanges.History(Context.Store, Context.BackupsDirectory()).FirstOrDefault(r => r.Scope == Scope);
        if (record is null)
        {
            Context.Dialogs.ShowInfo("되돌리기", "되돌릴 변경 기록이 없습니다.");
            return;
        }

        var undo = EnvironmentChanges.Revert(record, out var skipped);
        var note = skipped > 0 ? $" 비밀 변수 {skipped}개는 값을 기록하지 않아 되돌릴 수 없습니다." : string.Empty;
        if (undo.Count == 0)
        {
            Context.Dialogs.ShowInfo("되돌리기", "되돌릴 수 있는 변경이 없습니다." + note);
            return;
        }

        if (Review("마지막 변경 되돌리기", $"{record.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}의 변경 {undo.Count}개를 되돌립니다.{note}", undo))
        {
            Write(undo, "이전 환경변수 변경을 되돌렸습니다.");
        }
    }

    [RelayCommand]
    private void RestartAsAdministrator() => Context.RestartAsAdministrator();

    protected void OnEdited()
    {
        if (!Loading)
        {
            IsDirty = true;
            AfterEdit();
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvironmentRow.Name) or nameof(EnvironmentRow.Value))
        {
            OnEdited();
        }
    }
}

/// <summary>Table of the user or machine environment variables.</summary>
public sealed partial class EnvironmentDocumentViewModel : SystemDocumentViewModel
{
    public EnvironmentDocumentViewModel(EnvironmentScope scope, SystemContext context, Action<DocumentViewModel> close)
        : base(IdFor(scope), scope == EnvironmentScope.User ? "환경변수(사용자)" : "환경변수(시스템)", context, close)
    {
        Scope = scope;
        Reload();
    }

    public static string IdFor(EnvironmentScope scope) => scope == EnvironmentScope.User ? "doc:env-user" : "doc:env-machine";

    public IEnumerable<EnvironmentRow> VisibleRows => Filter.Trim().Length == 0
        ? Rows
        : Rows.Where(r => r.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
            || (!r.IsSecret && r.Value.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)));

    protected override IEnumerable<EnvironmentRow> LoadRows(IReadOnlyDictionary<string, string> values) =>
        values.Select(p => new EnvironmentRow { Name = p.Key, Value = p.Value });

    protected override IReadOnlyList<KeyValuePair<string, string>> Target(IReadOnlyDictionary<string, string> current) =>
        Rows.Select(r => new KeyValuePair<string, string>(r.Name, r.Value)).ToList();

    protected override string? BlockingError() =>
        Rows.Any(r => r.Issue == DuplicateName) ? "이름이 중복된 변수가 있습니다. 하나만 남기고 적용하세요." : null;

    private const string DuplicateName = "이름이 중복됩니다.";

    protected override void AfterEdit()
    {
        var names = Rows.GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            row.Issue = row.Name.Trim().Length == 0 ? "이름이 비어 있어 저장하지 않습니다." : names.Contains(row.Name.Trim()) ? DuplicateName : null;
        }

        OnPropertyChanged(nameof(VisibleRows));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Filter))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(VisibleRows)));
        }
    }

    [RelayCommand]
    private void Add()
    {
        if (CanWrite)
        {
            Filter = string.Empty;
            Rows.Add(new EnvironmentRow { Name = "NEW_VARIABLE" });
        }
    }
}

/// <summary>PATH editor: one row per entry, reorder, warnings for missing folders and duplicates.</summary>
public sealed partial class PathDocumentViewModel : SystemDocumentViewModel
{
    public const string Id = "doc:path";

    private string? _pathName;

    public PathDocumentViewModel(SystemContext context, Action<DocumentViewModel> close)
        : base(Id, "PATH", context, close)
    {
        Scope = EnvironmentScope.User;
        Reload();
    }

    protected override IEnumerable<EnvironmentRow> LoadRows(IReadOnlyDictionary<string, string> values)
    {
        _pathName = values.Keys.FirstOrDefault(k => string.Equals(k, "Path", StringComparison.OrdinalIgnoreCase));
        return PathList.Split(_pathName is null ? null : values[_pathName]).Select(p => new EnvironmentRow { Name = "Path", Value = p });
    }

    protected override IReadOnlyList<KeyValuePair<string, string>> Target(IReadOnlyDictionary<string, string> current)
    {
        var name = _pathName ?? "Path";
        var result = current.Where(p => !string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var joined = PathList.Join(Rows.Select(r => r.Value));
        if (joined.Length > 0)
        {
            result.Add(new(name, joined));
        }

        return result;
    }

    protected override void AfterEdit()
    {
        var entries = Rows.Select(r => r.Value).ToList();
        var issues = PathList.Analyze(entries, Context.Store.DirectoryExists, System.Environment.ExpandEnvironmentVariables);
        for (var i = 0; i < Rows.Count; i++)
        {
            var messages = issues.Where(x => x.Index == i).Select(x => x.Message).ToList();
            Rows[i].Issue = messages.Count == 0 ? null : string.Join(" ", messages);
        }
    }

    [RelayCommand]
    private void Add()
    {
        if (CanWrite)
        {
            Rows.Add(new EnvironmentRow { Name = "Path", Value = string.Empty });
        }
    }

    [RelayCommand]
    private void MoveUp(EnvironmentRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(EnvironmentRow? row) => Move(row, +1);

    private void Move(EnvironmentRow? row, int delta)
    {
        if (row is null || !CanWrite)
        {
            return;
        }

        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < Rows.Count)
        {
            Rows.Move(from, to);
        }
    }
}
