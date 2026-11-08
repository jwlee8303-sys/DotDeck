using DotDeck.Core.Files;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Editors.Registry;

/// <summary>Inputs captured before validation moves off the UI thread.</summary>
public sealed record EditorValidationContext(
    string Path,
    string Text,
    IFileStore Store,
    SettingsContext? Settings = null,
    SettingsKeyCatalog? Keys = null,
    ClaudeToolCatalog? Tools = null,
    HookEventCatalog? Events = null,
    FileRole? Role = null);

/// <summary>Stable persistence name and capabilities of one editor, independent of UI types.</summary>
public sealed record EditorDescriptor(
    EditorKind Kind,
    string Name,
    string Label,
    string? Product = null,
    bool HasForm = false,
    bool HasOutline = false,
    Func<EditorValidationContext, IReadOnlyList<ValidationIssue>>? Validate = null,
    Func<string, string?>? FormError = null)
{
    public bool CanShowForm(string text, out string? error)
    {
        error = HasForm ? FormError?.Invoke(text) : "이 형식의 폼은 아직 지원하지 않습니다.";
        return HasForm && error is null;
    }
}
