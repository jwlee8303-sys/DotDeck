using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Editors;

namespace DotDeck.App.ViewModels;

/// <summary>Live validation of the active typed document into the validation pane.</summary>
public sealed partial class ShellViewModel
{
    private static readonly TimeSpan s_validationDelay = TimeSpan.FromMilliseconds(400);

    private TextDocumentViewModel? _validated;
    private int _validationVersion;

    partial void OnActiveTextDocumentChanged(TextDocumentViewModel? value)
    {
        if (_validated is not null)
        {
            _validated.Buffer.TextChanged -= OnValidatedTextChanged;
        }

        _validated = value is { Kind: EditorKind.Agent or EditorKind.Skill or EditorKind.ClaudeSettings } ? value : null;
        if (_validated is null)
        {
            Validation.Clear();
            return;
        }

        _validated.Buffer.TextChanged += OnValidatedTextChanged;
        _ = ValidateAsync(immediate: true);
    }

    /// <summary>Loads a hook row of a settings form into the hook test pane and shows it.</summary>
    public void RunHook(Editor.HookRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        HookTest.Load(row.Event, row.Matcher, row.Command);
        HookTest.IsVisible = true;
        HookTest.IsActive = true;
    }

    private void OnValidatedTextChanged(object? sender, EventArgs e) => _ = ValidateAsync(immediate: false);

    private async Task ValidateAsync(bool immediate)
    {
        var version = ++_validationVersion;
        if (!immediate)
        {
            await Task.Delay(s_validationDelay).ConfigureAwait(true);
        }

        if (version != _validationVersion || _validated is not { } doc)
        {
            return;
        }

        var path = doc.FilePath;
        var text = doc.Buffer.Document.Text;
        var kind = doc.Kind;
        var store = _store;
        var issues = await Task.Run(() => Validate(kind, path, text, store)).ConfigureAwait(true);
        if (version == _validationVersion && _validated == doc)
        {
            Validation.Show(doc.Buffer.FileName, issues);
        }
    }

    private static IReadOnlyList<ValidationIssue> Validate(EditorKind kind, string path, string text, Core.Files.IFileStore store) => kind switch
    {
        EditorKind.Agent => MarkdownAgentValidator.ValidateAgent(path, text),
        EditorKind.Skill => MarkdownAgentValidator.ValidateSkill(path, text),
        EditorKind.ClaudeSettings => ClaudeSettingsDocument.TryParse(text, out var error) is { } settings
            ? settings.Validate(store.FileExists)
            : [new ValidationIssue(IssueSeverity.Error, error ?? "JSON 오류")],
        _ => [],
    };
}
