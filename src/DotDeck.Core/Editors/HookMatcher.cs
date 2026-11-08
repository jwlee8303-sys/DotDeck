using System.Text.RegularExpressions;

namespace DotDeck.Core.Editors;

public enum HookMatcherMode
{
    /// <summary><c>""</c>, <c>"*"</c> or no matcher: every value.</summary>
    All,

    /// <summary>Exact names separated by <c>|</c> (or <c>,</c>), possibly written as <c>^(a|b|mcp__s__.*)$</c>.</summary>
    List,

    /// <summary>Any other text: a JavaScript regular expression, not anchored.</summary>
    Regex,
}

/// <summary>How an event reads its matcher (https://code.claude.com/docs/en/hooks, "Matcher patterns").</summary>
public enum HookMatcherSyntax
{
    /// <summary>Letters, digits, <c>_</c>, <c>-</c>, spaces, <c>,</c> and <c>|</c> are exact names; anything else is a regex.</summary>
    Standard,

    /// <summary>FileChanged and StopFailure: only letters, digits, <c>_</c> and <c>|</c> are exact names.</summary>
    PipeOnly,

    /// <summary>
    /// FileChanged: the matcher is also split on <c>|</c> into literal file names to watch, so a list is written
    /// as plain <c>a|b</c> (never wrapped in <c>^(...)$</c>, which would watch a file of that name).
    /// </summary>
    FileNames,
}

/// <summary>
/// A hook matcher as a set of check boxes. Because a regex is not anchored, a list that includes a regex item (such
/// as <c>mcp__server__.*</c>) or a name with other characters is written as <c>^(A|B|mcp__server__.*)$</c> so that
/// <c>Read</c> does not also match <c>ReadMcpResourceTool</c>.
/// </summary>
public sealed partial record HookMatcher(HookMatcherMode Mode, IReadOnlyList<string> Items, string Separator, string Raw, HookMatcherSyntax Syntax)
{
    public bool Contains(string item) => Mode == HookMatcherMode.List && Items.Contains(item, StringComparer.Ordinal);

    public static HookMatcherSyntax SyntaxOf(HookEventInfo? info) =>
        info is null ? HookMatcherSyntax.Standard
        : info.FileNames ? HookMatcherSyntax.FileNames
        : info.PipeOnly ? HookMatcherSyntax.PipeOnly
        : HookMatcherSyntax.Standard;

    public static HookMatcher Parse(string? raw, HookMatcherSyntax syntax = HookMatcherSyntax.Standard)
    {
        var text = raw ?? string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed == "*")
        {
            return new HookMatcher(HookMatcherMode.All, [], "|", text, syntax);
        }

        if (syntax == HookMatcherSyntax.FileNames)
        {
            var files = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new HookMatcher(HookMatcherMode.List, files, "|", text, syntax);
        }

        // Pipe-only events treat spaces (also leading or trailing ones) as regex text.
        var pipeOnly = syntax == HookMatcherSyntax.PipeOnly;
        if (pipeOnly ? PipeOnlyList().IsMatch(text) : SimpleList().IsMatch(trimmed))
        {
            var separator = trimmed.Contains('|', StringComparison.Ordinal) || !trimmed.Contains(',', StringComparison.Ordinal) ? "|" : ",";
            var items = trimmed.Split(pipeOnly ? ['|'] : ['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new HookMatcher(HookMatcherMode.List, items, separator, text, syntax);
        }

        if (AnchoredList().Match(trimmed) is { Success: true } anchored)
        {
            var items = anchored.Groups["body"].Value.Split('|');
            if (items.All(i => IsName(i, syntax) || McpServerPattern().IsMatch(i)))
            {
                return new HookMatcher(HookMatcherMode.List, items, "|", text, syntax);
            }
        }

        return new HookMatcher(HookMatcherMode.Regex, [], "|", text, syntax);
    }

    /// <summary>Text for a set of items: empty for none, a plain list, or an anchored regex when an item is not a plain name.</summary>
    public static string Compose(IReadOnlyList<string> items, string separator = "|", HookMatcherSyntax syntax = HookMatcherSyntax.Standard)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (syntax == HookMatcherSyntax.FileNames)
        {
            return string.Join('|', items);
        }

        return items.All(i => IsName(i, syntax))
            ? string.Join(separator == "," && syntax == HookMatcherSyntax.Standard ? "," : "|", items)
            : $"^({string.Join('|', items)})$";
    }

    /// <summary>Adds or removes <paramref name="item"/>, keeping the order and separator of the rest.</summary>
    public string Toggle(string item, bool on)
    {
        var items = Mode == HookMatcherMode.List ? Items.ToList() : [];
        items.RemoveAll(i => string.Equals(i, item, StringComparison.Ordinal));
        if (on)
        {
            items.Add(item);
        }

        return Compose(items, Separator, Syntax);
    }

    /// <summary>A problem with a regex matcher (checked with .NET regex syntax, close to JavaScript), or null.</summary>
    public string? RegexError()
    {
        // FileChanged: the same text that lists the watched files also filters hook groups by file name, and a
        // name with characters outside letters, digits, _ and | is evaluated as a regex there.
        var filtersAsRegex = Mode == HookMatcherMode.Regex
            || Syntax == HookMatcherSyntax.FileNames && Mode == HookMatcherMode.List && !PipeOnlyList().IsMatch(Raw.Trim());
        if (!filtersAsRegex)
        {
            return null;
        }

        try
        {
            _ = new Regex(Raw.Trim(), RegexOptions.None, TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"matcher 정규식이 올바르지 않습니다: {ex.Message}";
        }
    }

    private static bool IsName(string item, HookMatcherSyntax syntax) =>
        syntax == HookMatcherSyntax.PipeOnly ? PipeOnlyName().IsMatch(item) : SimpleName().IsMatch(item);

    [GeneratedRegex(@"^[A-Za-z0-9_\- ,|]+$")]
    private static partial Regex SimpleList();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"^[A-Za-z0-9_|]+$")]
    private static partial Regex PipeOnlyList();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex PipeOnlyName();

    [GeneratedRegex(@"^\^\((?<body>[^()]+)\)\$$")]
    private static partial Regex AnchoredList();

    [GeneratedRegex(@"^mcp__[A-Za-z0-9_\-]+__\.\*$")]
    private static partial Regex McpServerPattern();
}
