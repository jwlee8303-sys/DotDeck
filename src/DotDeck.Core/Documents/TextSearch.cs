using System.Text.RegularExpressions;

namespace DotDeck.Core.Documents;

/// <summary>Find/replace options. <see cref="WholeWord"/> also applies to regular expressions.</summary>
public sealed record SearchOptions(string Pattern, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

/// <summary>A match in the searched text.</summary>
public readonly record struct TextMatch(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Find and replace over plain strings (the editor passes its document text).</summary>
public static class TextSearch
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    /// <summary>Builds the matcher. Returns null with an error message for an empty or invalid pattern.</summary>
    public static Regex? TryBuild(SearchOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        if (string.IsNullOrEmpty(options.Pattern))
        {
            error = "검색어가 비어 있습니다.";
            return null;
        }

        var pattern = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord)
        {
            pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        }

        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new Regex(pattern, flags, s_timeout);
        }
        catch (ArgumentException ex)
        {
            error = $"정규식 오류: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Next non-empty match at or after <paramref name="from"/> (or the last one ending at or before it when
    /// <paramref name="backward"/>), wrapping around the text. Null when nothing matches.
    /// </summary>
    public static TextMatch? FindNext(string text, Regex regex, int from, bool backward = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regex);
        from = Math.Clamp(from, 0, text.Length);
        var all = FindAll(text, regex);
        if (all.Count == 0)
        {
            return null;
        }

        if (!backward)
        {
            foreach (var m in all)
            {
                if (m.Start >= from)
                {
                    return m;
                }
            }

            return all[0];
        }

        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].End <= from)
            {
                return all[i];
            }
        }

        return all[^1];
    }

    /// <summary>All non-empty matches in order.</summary>
    public static IReadOnlyList<TextMatch> FindAll(string text, Regex regex)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regex);
        var result = new List<TextMatch>();
        for (var m = regex.Match(text); m.Success; m = m.NextMatch())
        {
            if (m.Length > 0)
            {
                result.Add(new TextMatch(m.Index, m.Length));
            }
        }

        return result;
    }

    /// <summary>
    /// Replacement text for the match at <paramref name="match"/>. Regex mode expands <c>$1</c>-style groups;
    /// plain mode inserts <paramref name="replacement"/> literally. Null if the text there no longer matches.
    /// </summary>
    public static string? ExpandReplacement(string text, Regex regex, TextMatch match, string replacement, bool useRegex)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(replacement);
        var m = regex.Match(text, match.Start);
        if (!m.Success || m.Index != match.Start || m.Length != match.Length)
        {
            return null;
        }

        return useRegex ? m.Result(replacement) : replacement;
    }

    /// <summary>Replaces every non-empty match. Returns the new text and the number of replacements.</summary>
    public static (string Text, int Count) ReplaceAll(string text, Regex regex, string replacement, bool useRegex)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(replacement);
        var count = 0;
        var result = regex.Replace(text, m =>
        {
            if (m.Length == 0)
            {
                return m.Value;
            }

            count++;
            return useRegex ? m.Result(replacement) : replacement;
        });
        return (result, count);
    }
}
