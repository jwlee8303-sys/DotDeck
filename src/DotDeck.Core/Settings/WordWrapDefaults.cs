using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.Core.Settings;

/// <summary>
/// Whether the editor's soft wrap ("자동 줄 바꿈") starts on for a file. Wrapping only changes how lines are shown,
/// never the text. The extension list comes from <see cref="AppSettings.WordWrapExtensions"/>.
/// </summary>
public static class WordWrapDefaults
{
    /// <summary>Extensions wrapped by default when the settings have no list of their own (prose, not code).</summary>
    public static IReadOnlyList<string> Preset { get; } = [".md", ".txt"];

    /// <summary>The list in effect: the settings' own list, else <see cref="Preset"/>.</summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? configured) => configured ?? Preset;

    /// <summary>
    /// True when <paramref name="path"/> ends with one of the extensions (case-insensitive). A file without an
    /// extension, or with one not on the list, starts unwrapped.
    /// </summary>
    public static bool IsEnabledFor(string? path, IReadOnlyList<string>? configured)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return name.Length > 0 && ExtensionRules.Matches(name, ExtensionRules.NormalizeAll(Effective(configured)));
    }
}
