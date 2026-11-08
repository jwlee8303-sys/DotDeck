using DotDeck.App.ViewModels.Documents;
using DotDeck.Core.Editors;
using DotDeck.Core.Editors.Registry;

namespace DotDeck.App.ViewModels;

/// <summary>Live validation of the active typed document into the validation pane.</summary>
public sealed partial class ShellViewModel
{
    private static readonly TimeSpan s_validationDelay = TimeSpan.FromMilliseconds(400);

    private TextDocumentViewModel? _validated;
    private int _validationVersion;

    partial void OnActiveTextDocumentChanged(TextDocumentViewModel? value) => RefreshValidationTarget();

    /// <summary>Re-evaluates what the validation pane follows (the active document, or its new editor kind).</summary>
    private void RefreshValidationTarget()
    {
        var value = ActiveTextDocument;
        if (_validated is not null)
        {
            _validated.Buffer.TextChanged -= OnValidatedTextChanged;
        }

        _validated = value is not null && EditorRegistry.Get(value.Kind).Validate is not null ? value : null;
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
        var context = Core.Editors.SettingsContext.From(doc.Role, Paths.Home);
        var keys = SettingsKeys;
        var tools = ToolCatalog;
        var events = HookEvents;
        var validation = new EditorValidationContext(path, text, store, context, keys, tools, events, doc.Role);
        var validate = EditorRegistry.Get(kind).Validate;
        var issues = await Task.Run(() => validate?.Invoke(validation) ?? []).ConfigureAwait(true);
        if (version == _validationVersion && _validated == doc)
        {
            Validation.Show(doc.Buffer.FileName, issues);
        }
    }

}
