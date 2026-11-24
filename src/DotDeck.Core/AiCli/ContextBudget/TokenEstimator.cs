using System.Globalization;
using System.Text;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>
/// Rough token count from characters (no tokenizer is shipped, so every number is shown as "추정"): ASCII text about
/// 4 characters per token, Hangul and CJK about 1 token per character, other letters (Cyrillic, accents, emoji parts)
/// about 2 characters per token. Whitespace runs count as one character.
/// </summary>
public static class TokenEstimator
{
    public const double AsciiPerToken = 4.0;
    public const double WidePerToken = 1.0;
    public const double OtherPerToken = 2.0;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double ascii = 0, wide = 0, other = 0;
        var previousSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var space = Rune.IsWhiteSpace(rune);
            if (space && previousSpace)
            {
                continue;
            }

            previousSpace = space;
            if (rune.Value < 0x80)
            {
                ascii++;
            }
            else if (IsWide(rune))
            {
                wide++;
            }
            else
            {
                other++;
            }
        }

        return (int)Math.Ceiling(ascii / AsciiPerToken + wide / WidePerToken + other / OtherPerToken);
    }

    /// <summary>"1.8만", "4,200" (Korean short form used by the tab).</summary>
    public static string Format(long tokens) => tokens >= 10_000
        ? (tokens / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "만"
        : tokens.ToString("N0", CultureInfo.InvariantCulture);

    private static bool IsWide(Rune rune) => rune.Value switch
    {
        >= 0xAC00 and <= 0xD7A3 => true, // Hangul syllables
        >= 0x1100 and <= 0x11FF => true, // Hangul jamo
        >= 0x3130 and <= 0x318F => true, // Hangul compatibility jamo
        >= 0x3040 and <= 0x30FF => true, // kana
        >= 0x3400 and <= 0x9FFF => true, // CJK ideographs
        >= 0xF900 and <= 0xFAFF => true,
        >= 0x20000 and <= 0x2FFFF => true,
        _ => false,
    };
}
