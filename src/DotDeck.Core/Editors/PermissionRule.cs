using System.Text.RegularExpressions;

namespace DotDeck.Core.Editors;

/// <summary>
/// A permission rule <c>Tool</c> or <c>Tool(argument)</c> (https://code.claude.com/docs/en/permissions). Parentheses
/// inside the argument are literal: the argument runs from the first <c>(</c> after the tool name to the final
/// <c>)</c>. Text that does not have this shape is kept as <see cref="Raw"/> and written back unchanged.
/// </summary>
public sealed partial record PermissionRule(string Raw, string Tool, string? Argument, bool IsParsed)
{
    public static PermissionRule Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (Shape().Match(raw) is { Success: true } m)
        {
            return new PermissionRule(raw, m.Groups["tool"].Value, m.Groups["arg"].Success ? m.Groups["arg"].Value : null, true);
        }

        return new PermissionRule(raw, raw, null, false);
    }

    /// <summary><c>Tool(argument)</c>, or the tool alone when the argument is empty.</summary>
    public static string Compose(string tool, string? argument)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Trim();
        return string.IsNullOrWhiteSpace(argument) ? name : $"{name}({argument.Trim()})";
    }

    public static bool IsMcp(string tool) => tool.StartsWith("mcp__", StringComparison.Ordinal);

    /// <summary>Rules Claude Code skips or never matches, and Windows path mistakes.</summary>
    public static IEnumerable<ValidationIssue> Lint(string kind, string raw, ClaudeToolCatalog? tools)
    {
        var rule = Parse(raw.Trim());
        if (!rule.IsParsed)
        {
            yield return new ValidationIssue(IssueSeverity.Info, $"권한 규칙 형식을 해석할 수 없어 원문 그대로 둡니다: {raw}");
            yield break;
        }

        if (IsMcp(rule.Tool))
        {
            if (rule.Argument is not null)
            {
                yield return new ValidationIssue(IssueSeverity.Warning, $"괄호가 붙은 mcp__ 규칙은 Claude Code가 건너뜁니다: {raw}");
            }

            if (kind == "allow" && rule.Tool == "mcp__*")
            {
                yield return new ValidationIssue(IssueSeverity.Warning, "allow의 \"mcp__*\"는 무시됩니다. mcp__서버 형식으로 서버를 지정하세요.");
            }

            yield break;
        }

        if (kind == "allow" && rule.Tool.Contains('*', StringComparison.Ordinal))
        {
            yield return new ValidationIssue(IssueSeverity.Warning, $"도구 이름의 *는 deny·ask에서만 쓸 수 있습니다: {raw}");
        }

        if (rule.Argument is { Length: > 0 } argument && tools?.Find(rule.Tool) is { Argument: "notMatched" })
        {
            yield return new ValidationIssue(IssueSeverity.Warning,
                $"{rule.Tool}(...) 규칙은 일치하지 않습니다. 쓰기는 Edit(경로), 검색은 Read(경로)로 쓰세요: {raw}");
        }

        if (rule.Argument is { } path && rule.Tool is "Read" or "Edit" && WindowsPath().IsMatch(path))
        {
            yield return new ValidationIssue(IssueSeverity.Warning,
                $"Windows 경로는 //c/... 형식으로 씁니다(드라이브 문자 소문자, / 구분): {raw}");
        }

        if (tools is not null && tools.Find(rule.Tool) is null && rule.Tool != "*")
        {
            yield return new ValidationIssue(IssueSeverity.Info, $"도구 목록에 없는 이름입니다(그대로 저장): {rule.Tool}");
        }
    }

    [GeneratedRegex(@"^(?<tool>[^\s()]+)(?:\((?<arg>.*)\))?$", RegexOptions.Singleline)]
    private static partial Regex Shape();

    // C:\x, C:/x, /C:/x
    [GeneratedRegex(@"^/?[A-Za-z]:[\\/]")]
    private static partial Regex WindowsPath();
}
