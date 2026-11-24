using System.Text.RegularExpressions;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Catalogs;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>One approval prompt as it looks on screen (<c>Catalogs/Approvals/screen-prompts.json</c>).</summary>
public sealed record ScreenPromptDefinition
{
    public CliKind Cli { get; init; }

    /// <summary>The CLI version the pattern and keys were tried on; keys are sent only to this exact version.</summary>
    public string? VerifiedVersion { get; init; }

    public string? Verified { get; init; }

    public string? Note { get; init; }

    public string Question { get; init; } = string.Empty;

    public IReadOnlyList<string> Options { get; init; } = [];

    public string? AllowKeys { get; init; }

    public string? DenyKeys { get; init; }
}

/// <summary>The definitions file.</summary>
public sealed record ScreenPromptCatalog
{
    public IReadOnlyList<ScreenPromptDefinition> Prompts { get; init; } = [];

    public const string RelativePath = "Approvals/screen-prompts.json";

    public static ScreenPromptCatalog BuiltIn() => Parse(CatalogResources.ReadBuiltIn(RelativePath));

    public static ScreenPromptCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<ScreenPromptCatalog>(json);
        foreach (var prompt in catalog.Prompts)
        {
            // Throws ArgumentException for an invalid pattern: a broken file is not used.
            _ = new Regex(prompt.Question);
            foreach (var option in prompt.Options)
            {
                _ = new Regex(option);
            }
        }

        return catalog;
    }
}

/// <summary>What was found on the screen.</summary>
/// <param name="Question">The prompt's question line ("Do you want to proceed?").</param>
/// <param name="Context">The lines above the question (tool, command), masked.</param>
public sealed record ScreenPromptMatch(ScreenPromptDefinition Definition, string Question, string Context)
{
    /// <summary>Identifies the same prompt in a later read (re-check before keys are sent).</summary>
    public string Fingerprint => Question + "\n" + Context;
}

/// <summary>
/// Fallback detection for one CLI: an approval prompt is assumed only when its pattern is on the screen AND the output
/// has stopped for <see cref="QuietTime"/>. Answers are key presses, sent only when the installed version is the
/// verified one and the same prompt is still on the screen right before sending.
/// </summary>
public sealed class ScreenApprovalProvider : IApprovalProvider
{
    /// <summary>Output must have stopped this long before a pattern match counts.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(2);

    /// <summary>Only the bottom of the screen is searched (prompts are drawn at the bottom).</summary>
    public const int SearchLines = 30;

    private const int ContextLines = 8;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<(ScreenPromptDefinition Definition, Regex Question, Regex[] Options)> _prompts;

    public ScreenApprovalProvider(CliKind cli, ScreenPromptCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Cli = cli;
        _prompts = [.. catalog.Prompts.Where(p => p.Cli == cli).Select(p => (
            p,
            new Regex(p.Question, RegexOptions.CultureInvariant, MatchTimeout),
            p.Options.Select(o => new Regex(o, RegexOptions.CultureInvariant, MatchTimeout)).ToArray()))];
    }

    public CliKind Cli { get; }

    public ApprovalSource Source => ApprovalSource.Screen;

    /// <summary>The prompt on <paramref name="screen"/> (terminal lines, top to bottom), or null.</summary>
    public ScreenPromptMatch? Match(string? screen)
    {
        if (string.IsNullOrEmpty(screen))
        {
            return null;
        }

        var lines = screen.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var bottom = lines.Skip(Math.Max(0, lines.Length - SearchLines)).ToArray();
        var text = string.Join("\n", bottom);
        try
        {
            foreach (var (definition, question, options) in _prompts)
            {
                // One prompt block: the last question line, and every option on the lines below it (options elsewhere
                // on the screen, e.g. above the question, on the question line itself or from an earlier prompt, do
                // not count).
                var questionLine = Array.FindLastIndex(bottom, l => question.IsMatch(l));
                if (questionLine < 0)
                {
                    continue;
                }

                var block = string.Join("\n", bottom.Skip(questionLine + 1));
                if (!options.All(o => o.IsMatch(block)))
                {
                    continue;
                }

                var found = question.Match(bottom[questionLine]);
                var context = string.Join("\n", bottom.Take(questionLine).Select(Clean).Where(l => l.Length > 0).TakeLast(ContextLines));
                return new ScreenPromptMatch(definition, found.Value.Trim(), SecretMasker.MaskText(context));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Keys answering <paramref name="match"/>, or null when DotDeck must not type anything: the installed version is
    /// unknown or not the verified one, or the definition has no keys for the decision.
    /// </summary>
    public static string? KeysFor(ScreenPromptMatch match, ApprovalDecision decision, string? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!CanAnswer(match.Definition, installedVersion))
        {
            return null;
        }

        return decision == ApprovalDecision.Allow ? match.Definition.AllowKeys : match.Definition.DenyKeys;
    }

    /// <summary>Keys may be typed: both keys are defined and the installed version is exactly the verified one.</summary>
    public static bool CanAnswer(ScreenPromptDefinition definition, string? installedVersion) =>
        definition is { AllowKeys.Length: > 0, DenyKeys.Length: > 0, VerifiedVersion: { } verified }
        && installedVersion is not null
        && string.Equals(verified, installedVersion, StringComparison.Ordinal);

    /// <summary>
    /// "2.1.330 (Claude Code)", "codex-cli 0.171.0", "1.5.15" → the first x.y.z with its pre-release/build suffix
    /// ("0.171.0-beta.1" stays as it is, so it never equals a verified release), or null.
    /// </summary>
    public static string? ParseVersion(string? output)
    {
        var match = Regex.Match(
            output ?? string.Empty,
            @"(?<![\w.])(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![\w.])",
            RegexOptions.None,
            MatchTimeout);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Creates the request DotDeck shows for a screen match. <paramref name="live"/> is false when the screen was
    /// rendered from recent output because the tab is not shown: such requests are never answered with keys.
    /// </summary>
    public ApprovalRequest ToRequest(ScreenPromptMatch match, string sessionKey, string? workingDirectory, string? installedVersion, DateTimeOffset now, bool live = true)
    {
        ArgumentNullException.ThrowIfNull(match);
        var canAnswer = live && CanAnswer(match.Definition, installedVersion);
        return new ApprovalRequest(sessionKey, Cli, ApprovalSource.Screen, match.Question, match.Context, workingDirectory, now)
        {
            CanRespond = canAnswer,
            MatchKey = match.Fingerprint,
            CannotRespondReason = canAnswer ? null
                : !live ? "탭이 표시되지 않아 화면을 다시 확인할 수 없습니다 · 터미널로 이동해 응답하세요"
                : installedVersion is null ? "CLI 버전을 확인하지 못해 터미널에서 응답하세요"
                : $"확인한 버전({match.Definition.VerifiedVersion})과 달라({installedVersion}) 터미널에서 응답하세요",
        };
    }

    // Box drawing and prompt markers carry no information.
    private static string Clean(string line) => Regex.Replace(line, @"[─━│┃┌┐└┘├┤┬┴┼╭╮╯╰═║╌╍▌▐█▀▄]+", " ", RegexOptions.None, MatchTimeout).Trim();
}
