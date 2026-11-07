using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DotDeck.Core.Security;

/// <summary>
/// Masks values whose names look secret (KEY, TOKEN, SECRET, PASSWORD, PWD, CREDENTIAL).
/// Used for environment variable display, logs and backup manifests. Masking is idempotent.
/// When in doubt (unterminated quotes, time budget exceeded) it masks more, never less.
/// </summary>
public static partial class SecretMasker
{
    /// <summary>Fixed-length placeholder so the original value length is not leaked.</summary>
    public const string Mask = "••••••••";

    /// <summary>Returned instead of the original text when masking cannot complete safely.</summary>
    public const string Unmaskable = "[redacted: text could not be masked]";

    /// <summary>Total time budget for one <see cref="MaskText"/> call (all passes and matches).</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromMilliseconds(500);

    private const int RegexTimeoutMs = 250;

    private const string MaskedDoubleQuoted = "\"" + Mask + "\"";
    private const string MaskedSingleQuoted = "'" + Mask + "'";

    [GeneratedRegex("KEY|TOKEN|SECRET|PASSWORD|PWD|CREDENTIAL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretNameRegex();

    // name = "value" / "name": "value" / name: 'value'. Values honor backslash escapes and may span lines.
    // The lookbehind anchors names to token starts, keeping the scan linear on long words.
    [GeneratedRegex(
        """(?<![\w.\-])(?<nq>["']?)(?<name>[A-Za-z_][\w.\-]*)\k<nq>\s*[:=]\s*(?:"(?<value>(?:\\.|[^"\\])*)"|'(?<value>(?:\\.|[^'\\])*)')""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMs)]
    private static partial Regex QuotedAssignmentRegex();

    // name=value / name: value (unquoted, up to whitespace or delimiter).
    // A value that still starts with a quote here was not closed: mask it to the end of the line.
    [GeneratedRegex(
        """(?<![\w.\-])(?<name>[A-Za-z_][\w.\-]*)\s*[:=][ \t]*(?<value>["'][^\r\n]*|[^\s"';,]+)""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMs)]
    private static partial Regex BareAssignmentRegex();

    // Authorization: Bearer xxx / Basic xxx (credential has no secret-looking name)
    [GeneratedRegex(
        """\b(?:Bearer|Basic|Digest)[ \t]+(?<value>[A-Za-z0-9\-._~+/]+=*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMs)]
    private static partial Regex AuthSchemeRegex();

    public static bool IsSecretName(string? name) =>
        !string.IsNullOrEmpty(name) && SecretNameRegex().IsMatch(name);

    /// <summary>Returns <see cref="Mask"/> when <paramref name="name"/> is secret, otherwise the value unchanged.</summary>
    public static string? MaskValue(string? name, string? value) =>
        IsSecretName(name) && !string.IsNullOrEmpty(value) ? Mask : value;

    /// <summary>Masks secret-looking assignments (<c>NAME=value</c>, <c>"name": "value"</c>) inside free text.</summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var budget = Stopwatch.StartNew();
        try
        {
            var result = MaskAssignments(QuotedAssignmentRegex(), text, requireSecretName: true, budget);
            result = MaskAssignments(BareAssignmentRegex(), result, requireSecretName: true, budget);
            return MaskAssignments(AuthSchemeRegex(), result, requireSecretName: false, budget);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or TimeoutException)
        {
            // Never fall back to the original text: it may contain secrets.
            return Unmaskable;
        }
    }

    /// <summary>
    /// Replaces secret values. When a match's name is not secret, scanning resumes at its value
    /// so nested assignments such as <c>Exception: token=abc</c> are still found.
    /// </summary>
    private static string MaskAssignments(Regex regex, string text, bool requireSecretName, Stopwatch budget)
    {
        var sb = new StringBuilder(text.Length);
        var copied = 0;
        var position = 0;
        while (position <= text.Length)
        {
            if (budget.Elapsed > TimeBudget)
            {
                throw new TimeoutException();
            }

            var match = regex.Match(text, position);
            if (!match.Success)
            {
                break;
            }

            var value = match.Groups["value"];
            var maskable = (!requireSecretName || IsSecretName(match.Groups["name"].Value))
                && !IsAlreadyMasked(text, value.Index);
            if (maskable)
            {
                sb.Append(text, copied, value.Index - copied).Append(Mask);
                copied = value.Index + value.Length;
                position = Math.Max(copied, match.Index + 1);
            }
            else
            {
                // value.Index > match.Index always (a name precedes the value), so this advances.
                position = Math.Max(value.Index, match.Index + 1);
            }
        }

        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool IsAlreadyMasked(string text, int index) =>
        text.AsSpan(index).StartsWith(Mask, StringComparison.Ordinal)
        || text.AsSpan(index).StartsWith(MaskedDoubleQuoted, StringComparison.Ordinal)
        || text.AsSpan(index).StartsWith(MaskedSingleQuoted, StringComparison.Ordinal);
}
