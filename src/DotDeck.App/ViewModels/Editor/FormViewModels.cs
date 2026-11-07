using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Editors;

namespace DotDeck.App.ViewModels.Editor;

/// <summary>
/// Form view of a document (SPEC §7). The form edits a parsed model and writes the whole text back through
/// <c>apply</c>; the buffer stays the single source of truth, so saving, diff review and undo work as for text.
/// </summary>
public abstract class DocumentFormViewModel : ObservableObject
{
    private readonly Action<string> _apply;

    protected DocumentFormViewModel(Action<string> apply) => _apply = apply;

    /// <summary>True while <see cref="Load"/> fills the form (changes then are not written back).</summary>
    protected bool IsLoading { get; private set; }

    /// <summary>Fills the form from text. Returns an error message when the text cannot be shown as a form.</summary>
    public string? Load(string text)
    {
        IsLoading = true;
        try
        {
            return LoadCore(text);
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected abstract string? LoadCore(string text);

    protected void Apply(string text)
    {
        if (!IsLoading)
        {
            _apply(text);
        }
    }
}

/// <summary>agents/*.md and skills/*/SKILL.md: frontmatter fields plus the Markdown body.</summary>
public sealed partial class FrontmatterFormViewModel : DocumentFormViewModel
{
    public static readonly IReadOnlyList<string> KnownTools =
        ["Read", "Write", "Edit", "MultiEdit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch", "Task", "TodoWrite", "NotebookEdit"];

    private FrontmatterDocument _doc = FrontmatterDocument.Parse(string.Empty);

    public FrontmatterFormViewModel(EditorKind kind, Action<string> apply)
        : base(apply)
    {
        IsAgent = kind == EditorKind.Agent;
    }

    public bool IsAgent { get; }

    public IReadOnlyList<string> Models => MarkdownAgentValidator.KnownModels;

    public IReadOnlyList<string> Tools => KnownTools;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _toolList = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _body = string.Empty;

    protected override string? LoadCore(string text)
    {
        _doc = FrontmatterDocument.Parse(text);
        if (_doc.ParseIssues.FirstOrDefault(i => i.Severity == IssueSeverity.Error) is { } error)
        {
            return error.Message;
        }

        Name = _doc.Get("name") ?? string.Empty;
        Description = (_doc.Get("description") ?? string.Empty).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        ToolList = string.Join(", ", _doc.GetList("tools"));
        Model = _doc.Get("model") ?? string.Empty;
        Body = _doc.Body;
        return null;
    }

    [RelayCommand]
    private void AddTool(string? tool)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return;
        }

        var tools = SplitTools(ToolList);
        if (!tools.Contains(tool, StringComparer.Ordinal))
        {
            tools.Add(tool);
            ToolList = string.Join(", ", tools);
        }
    }

    [RelayCommand]
    private void SetModel(string? model) => Model = model ?? string.Empty;

    partial void OnNameChanged(string value) => Write(() => _doc.Set("name", value));

    partial void OnDescriptionChanged(string value) =>
        Write(() => _doc.Set("description", value.Replace("\r\n", "\n", StringComparison.Ordinal)));

    // Optional fields disappear when cleared (absent means "inherit all tools" / "default model").
    partial void OnToolListChanged(string value) => Write(() => _doc.SetList("tools", SplitTools(value)));

    partial void OnModelChanged(string value) => Write(() => _doc.Set("model", value.Trim().Length == 0 ? null : value.Trim()));

    partial void OnBodyChanged(string value) => Write(() => _doc.Body = value);

    private void Write(Action change)
    {
        if (IsLoading)
        {
            return;
        }

        change();
        Apply(_doc.ToString());
    }

    private static List<string> SplitTools(string value) =>
        value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
}

/// <summary>A row of a form table; any edit asks the owner to write the document back.</summary>
public abstract class FormRow : ObservableObject
{
    internal Action? Changed { get; set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Changed?.Invoke();
    }
}

public sealed partial class PermissionRow : FormRow
{
    [ObservableProperty]
    private string _kind = "allow";

    [ObservableProperty]
    private string _rule = string.Empty;
}

public sealed partial class HookRow : FormRow
{
    [ObservableProperty]
    private string _event = "PreToolUse";

    [ObservableProperty]
    private string _matcher = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _timeout = string.Empty;

    internal HookRule? Source { get; init; }
}

public sealed partial class EnvRow : FormRow
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>Claude Code settings.json: permissions, hooks and env tables. Other keys are kept untouched.</summary>
public sealed partial class SettingsFormViewModel : DocumentFormViewModel
{
    private ClaudeSettingsDocument? _doc;

    public SettingsFormViewModel(Action<string> apply)
        : base(apply)
    {
        foreach (var collection in new INotifyCollectionChanged[] { Permissions, Hooks, Env })
        {
            collection.CollectionChanged += (_, _) => Commit();
        }
    }

    public IReadOnlyList<string> PermissionKinds => ClaudeSettingsDocument.PermissionKinds;

    public IReadOnlyList<string> HookEvents => ClaudeSettingsDocument.HookEvents;

    public ObservableCollection<PermissionRow> Permissions { get; } = [];

    public ObservableCollection<HookRow> Hooks { get; } = [];

    public ObservableCollection<EnvRow> Env { get; } = [];

    /// <summary>Raised by <see cref="ShellViewModel"/> to run a hook row in the hook test pane.</summary>
    public Action<HookRow>? RunHook { get; set; }

    protected override string? LoadCore(string text)
    {
        var doc = ClaudeSettingsDocument.TryParse(text, out var error);
        if (doc is null)
        {
            return error + " JSON을 고친 뒤 폼으로 전환할 수 있습니다.";
        }

        _doc = doc;
        Reset(Permissions, ClaudeSettingsDocument.PermissionKinds
            .SelectMany(kind => doc.GetPermissions(kind).Select(rule => new PermissionRow { Kind = kind, Rule = rule })));
        Reset(Hooks, doc.GetHooks().Select(h => new HookRow
        {
            Event = h.Event,
            Matcher = h.Matcher,
            Command = h.Command,
            Timeout = h.Timeout?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Source = h,
        }));
        Reset(Env, doc.GetEnv().Select(p => new EnvRow { Key = p.Key, Value = p.Value }));
        return null;
    }

    [RelayCommand]
    private void AddPermission(string? kind) => Permissions.Add(Wire(new PermissionRow { Kind = kind ?? "allow" }));

    [RelayCommand]
    private void AddHook(string? evt) => Hooks.Add(Wire(new HookRow { Event = evt ?? "PreToolUse" }));

    [RelayCommand]
    private void AddEnv() => Env.Add(Wire(new EnvRow()));

    [RelayCommand]
    private void Remove(FormRow? row)
    {
        switch (row)
        {
            case PermissionRow p: Permissions.Remove(p); break;
            case HookRow h: Hooks.Remove(h); break;
            case EnvRow e: Env.Remove(e); break;
        }
    }

    [RelayCommand]
    private void MoveUp(FormRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(FormRow? row) => Move(row, +1);

    [RelayCommand]
    private void Run(HookRow? row)
    {
        if (row is not null)
        {
            RunHook?.Invoke(row);
        }
    }

    private void Move(FormRow? row, int delta)
    {
        switch (row)
        {
            case PermissionRow p: Shift(Permissions, p, delta); break;
            case HookRow h: Shift(Hooks, h, delta); break;
            case EnvRow e: Shift(Env, e, delta); break;
        }
    }

    private static void Shift<T>(ObservableCollection<T> list, T item, int delta)
    {
        var from = list.IndexOf(item);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < list.Count)
        {
            list.Move(from, to);
        }
    }

    private void Reset<T>(ObservableCollection<T> target, IEnumerable<T> rows)
        where T : FormRow
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(Wire(row));
        }
    }

    private T Wire<T>(T row)
        where T : FormRow
    {
        row.Changed = Commit;
        return row;
    }

    private void Commit()
    {
        if (IsLoading || _doc is null)
        {
            return;
        }

        foreach (var kind in ClaudeSettingsDocument.PermissionKinds)
        {
            _doc.SetPermissions(kind, Permissions.Where(p => p.Kind == kind && p.Rule.Trim().Length > 0).Select(p => p.Rule.Trim()).ToList());
        }

        _doc.SetHooks(Hooks.Where(h => h.Event.Trim().Length > 0)
            .Select(h => new HookRule(
                h.Event.Trim(),
                h.Matcher.Trim(),
                h.Command,
                int.TryParse(h.Timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null,
                h.Source?.CommandExtra,
                h.Source?.GroupExtra))
            .ToList());
        _doc.SetEnv(Env.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToList());
        Apply(_doc.ToJson());
    }
}
