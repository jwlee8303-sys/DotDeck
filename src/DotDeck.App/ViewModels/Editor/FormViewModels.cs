using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.Editors;
using DotDeck.Core.Roles;

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
    /// <summary>Fallback when the tool catalog is unavailable (normally the catalog's common tools are offered).</summary>
    public static IReadOnlyList<string> KnownTools => FrontmatterProfiles.ClaudeAgent.Tools;

    private FrontmatterDocument _doc = FrontmatterDocument.Parse(string.Empty);

    private readonly IReadOnlyList<string> _tools;

    public FrontmatterFormViewModel(EditorKind kind, Action<string> apply, IReadOnlyList<string>? tools = null)
        : this(FrontmatterProfiles.For(kind), apply, tools)
    {
    }

    public FrontmatterFormViewModel(FrontmatterProfile profile, Action<string> apply, IReadOnlyList<string>? tools = null)
        : base(apply)
    {
        Profile = profile;
        _tools = tools ?? profile.Tools;
        ToolListValues = new StringListViewModel { Changed = OnToolsChanged };
    }

    public FrontmatterProfile Profile { get; }

    public bool IsAgent => Profile.ShowAgentFields;

    public IReadOnlyList<string> Models => Profile.Models;

    public IReadOnlyList<string> Tools => _tools;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public StringListViewModel ToolListValues { get; }

    public string ToolList
    {
        get => ToolListValues.Text;
        set => ToolListValues.Text = value;
    }

    private void OnToolsChanged()
    {
        OnPropertyChanged(nameof(ToolList));
        Write(() => _doc.SetList("tools", ToolListValues.Values));
    }

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
        ToolListValues.Load(_doc.GetList("tools"));
        OnPropertyChanged(nameof(ToolList));
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

        ToolListValues.AddUnique(tool);
    }

    [RelayCommand]
    private void SetModel(string? model) => Model = model ?? string.Empty;

    partial void OnNameChanged(string value) => Write(() => _doc.Set("name", value));

    partial void OnDescriptionChanged(string value) =>
        Write(() => _doc.Set("description", value.Replace("\r\n", "\n", StringComparison.Ordinal)));

    // Optional fields disappear when cleared (absent means "inherit all tools" / "default model").
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

}

/// <summary>A row of a form table; any edit asks the owner to write the document back.</summary>
public abstract class FormRow : ObservableObject
{
    internal Action<FormRow, string?>? Changed { get; set; }

    /// <summary>Properties that only describe the row (help, placeholders) and never change the document.</summary>
    protected virtual bool IsDisplayOnly(string? propertyName) => false;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!IsDisplayOnly(e.PropertyName))
        {
            Changed?.Invoke(this, e.PropertyName);
        }
    }
}

/// <summary>A suggestion in a ▾ menu. Items of one <paramref name="Group"/> are listed together.</summary>
public sealed record ChoiceItem(string Value, string Label, string? Description = null, string? Group = null);

/// <summary>A check box of the hook matcher menu.</summary>
public sealed record MatcherChoice(string Value, string Label, string? Description, bool IsChecked, string? Group);
