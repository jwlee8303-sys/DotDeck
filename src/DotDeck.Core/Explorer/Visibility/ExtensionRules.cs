namespace DotDeck.Core.Explorer.Visibility;

/// <summary>
/// Default file extensions of the explorer. Entries are lower case and start with <c>.</c>; multi-part entries such
/// as <c>.d.ts</c> are allowed, and a file matches when its name ends with an entry (case-insensitive).
/// </summary>
public static class ExtensionRules
{
    /// <summary>The "설정·문서" preset and the built-in global default.</summary>
    public static IReadOnlyList<string> ConfigDocsPreset { get; } =
        [".md", ".json", ".jsonc", ".toml", ".yaml", ".yml", ".ps1", ".py", ".cs", ".xaml", ".props", ".txt"];

    private static readonly char[] s_invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>".LOG", "log", "Log", "*.log" → ".log"; ".d.ts" stays. Null for empty or invalid text.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var text = input.Trim();
        if (text.StartsWith('*'))
        {
            text = text[1..];
        }

        text = text.TrimStart('.');
        if (text.Length == 0 || text.IndexOfAny(s_invalid) >= 0 || text.Any(char.IsWhiteSpace) || text.EndsWith('.') || text.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return "." + text.ToLowerInvariant();
    }

    /// <summary>Normalized, distinct, sorted list (invalid entries dropped).</summary>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? inputs) =>
        (inputs ?? []).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public static bool Matches(string fileName, IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(extensions);
        foreach (var extension in extensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
