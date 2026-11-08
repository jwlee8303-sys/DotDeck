using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.App.ViewModels.Explorer;

/// <summary>App-wide explorer settings that can change while running (the settings dialog).</summary>
public sealed class ExplorerPreferences
{
    private IReadOnlyList<string> _globalExtensions = ExtensionRules.ConfigDocsPreset;

    /// <summary>Default extensions of projects that did not choose their own list (SPEC 2.3).</summary>
    public IReadOnlyList<string> GlobalExtensions => _globalExtensions;

    /// <summary>Raised on the UI thread after <see cref="GlobalExtensions"/> changed.</summary>
    public event EventHandler? Changed;

    /// <param name="extensions">Null means the "설정·문서" preset.</param>
    public void SetGlobalExtensions(IReadOnlyList<string>? extensions)
    {
        var next = extensions ?? ExtensionRules.ConfigDocsPreset;
        if (next.SequenceEqual(_globalExtensions, StringComparer.Ordinal))
        {
            return;
        }

        _globalExtensions = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
