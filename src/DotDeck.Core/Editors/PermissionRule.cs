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

    /// <summary>
    /// A deny/ask parameter rule <c>Tool(name:value)</c>: only names the catalog lists for the tool count, so
    /// <c>Bash(git:*)</c> and <c>WebFetch(domain:x)</c> stay ordinary arguments.
    /// </summary>
    public static PermissionParameter? ParameterOf(string tool, string? argument, ClaudeToolCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (argument is null || catalog.Find(tool.Trim()) is not { } info || ParameterShape().Match(argument.Trim()) is not { Success: true } m)
        {
            return null;
        }

        var name = m.Groups["name"].Value;
        if (info.ExcludedParameters.Contains(name, StringComparer.Ordinal))
        {
            return new PermissionParameter(name, m.Groups["value"].Value, IsExcluded: true);
        }

        return info.Parameters.Contains(name, StringComparer.Ordinal) ? new PermissionParameter(name, m.Groups["value"].Value, IsExcluded: false) : null;
    }

    /// <summary>Rules Claude Code skips or never matches, and Windows path mistakes (one rule, no cross-rule checks).</summary>
    public static IEnumerable<ValidationIssue> Lint(string kind, string raw, ClaudeToolCatalog? tools) =>
        PermissionLint.Check([new PermissionEntry(kind, raw)], tools).Select(i => i.Issue);

    [GeneratedRegex(@"^(?<tool>[^\s()]+)(?:\((?<arg>.*)\))?$", RegexOptions.Singleline)]
    private static partial Regex Shape();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*):(?<value>.*)$", RegexOptions.Singleline)]
    private static partial Regex ParameterShape();
}

/// <param name="IsExcluded">A field Claude Code refuses in parameter rules (<c>Bash(command:...)</c>).</param>
public sealed record PermissionParameter(string Name, string Value, bool IsExcluded);

public enum McpRuleScope
{
    /// <summary><c>mcp__server</c>: every tool of the server.</summary>
    Server,

    /// <summary><c>mcp__server__*</c>: every tool (same as <see cref="Server"/>).</summary>
    AllTools,

    /// <summary><c>mcp__server__tool</c>: one tool.</summary>
    Tool,
}

/// <summary>An MCP permission tool name split into server and scope.</summary>
public sealed record McpRuleParts(string Server, McpRuleScope Scope, string ToolName = "")
{
    /// <summary>
    /// The rule text. A server name with <c>__</c> cannot be written bare (<c>mcp__a__b</c> reads as server a, tool
    /// b), so "server" is written as <c>mcp__a__b__*</c> for it.
    /// </summary>
    public string ToolText => Scope switch
    {
        McpRuleScope.AllTools => $"mcp__{Server}__*",
        McpRuleScope.Tool => $"mcp__{Server}__{ToolName}",
        _ when Server.Contains("__", StringComparison.Ordinal) => $"mcp__{Server}__*",
        _ => $"mcp__{Server}",
    };

    /// <summary>
    /// Splits <c>mcp__server[__tool]</c>. A known server name wins (names may contain <c>__</c>), otherwise the first
    /// <c>__</c> after the prefix ends the server name. Null for non-MCP or empty names.
    /// </summary>
    public static McpRuleParts? Parse(string tool, IEnumerable<string>? knownServers = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Trim();
        if (!PermissionRule.IsMcp(name) || name.Length == 5)
        {
            return null;
        }

        var rest = name[5..];
        foreach (var server in (knownServers ?? []).Where(s => s.Length > 0).OrderByDescending(s => s.Length))
        {
            if (string.Equals(rest, server, StringComparison.Ordinal))
            {
                return new McpRuleParts(server, McpRuleScope.Server);
            }

            if (rest.StartsWith(server + "__", StringComparison.Ordinal))
            {
                return WithTool(server, rest[(server.Length + 2)..]);
            }
        }

        var separator = rest.IndexOf("__", StringComparison.Ordinal);
        return separator <= 0 ? new McpRuleParts(rest, McpRuleScope.Server) : WithTool(rest[..separator], rest[(separator + 2)..]);
    }

    private static McpRuleParts WithTool(string server, string tool) =>
        tool == "*" ? new McpRuleParts(server, McpRuleScope.AllTools) : new McpRuleParts(server, McpRuleScope.Tool, tool);
}

public sealed record PermissionEntry(string Kind, string Raw);

/// <param name="Index">The rule the issue is about.</param>
/// <param name="OtherIndex">The other rule of a duplicate or conflict (marked too, listed once).</param>
public sealed record PermissionIssue(int Index, ValidationIssue Issue, int? OtherIndex = null);

/// <summary>
/// Permission rule warnings (https://code.claude.com/docs/en/permissions). Nothing here blocks saving; the messages
/// come from <c>tools.json</c> → <c>messages</c>.
/// </summary>
public static partial class PermissionLint
{
    public static IReadOnlyList<PermissionIssue> Check(IReadOnlyList<PermissionEntry> rules, ClaudeToolCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var c = catalog ?? ClaudeToolCatalog.BuiltIn;
        var issues = new List<PermissionIssue>();
        var parsed = rules.Select(r => PermissionRule.Parse(r.Raw.Trim())).ToList();

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
        {
            var (kind, raw) = (rules[i].Kind, rules[i].Raw);
            if (seen.TryGetValue(raw, out var first))
            {
                var other = rules[first].Kind;
                issues.Add(other == kind
                    ? new PermissionIssue(i, new ValidationIssue(IssueSeverity.Warning, c.Message("duplicate", ("kind", kind), ("rule", raw))), first)
                    : new PermissionIssue(i, new ValidationIssue(IssueSeverity.Error, c.Message("conflict", ("other", other), ("kind", kind), ("rule", raw))), first));
            }
            else
            {
                seen[raw] = i;
            }
        }

        for (var i = 0; i < rules.Count; i++)
        {
            issues.AddRange(CheckRule(rules[i].Kind, rules[i].Raw, parsed[i], c).Select(issue => new PermissionIssue(i, issue)));
        }

        // A Read deny also blocks Edit/Write on the path; an Edit deny does not block reading it.
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].Kind != "deny" || parsed[i] is not { IsParsed: true, Tool: "Edit" } edit || PermissionRule.ParameterOf("Edit", edit.Argument, c) is not null)
            {
                continue;
            }

            var path = Normalize(edit.Argument);
            var covered = Enumerable.Range(0, rules.Count).Any(j =>
                rules[j].Kind == "deny" && parsed[j] is { IsParsed: true, Tool: "Read" } read
                && (Normalize(read.Argument) is null || Normalize(read.Argument) == path));
            if (!covered)
            {
                issues.Add(new PermissionIssue(i, new ValidationIssue(IssueSeverity.Info,
                    c.Message("editDenyOnly", ("readRule", PermissionRule.Compose("Read", path)), ("rule", rules[i].Raw)))));
            }
        }

        return issues;
    }

    private static string? Normalize(string? argument) => string.IsNullOrWhiteSpace(argument) || argument.Trim() == "*" ? null : argument.Trim();

    private static IEnumerable<ValidationIssue> CheckRule(string kind, string raw, PermissionRule rule, ClaudeToolCatalog c)
    {
        if (!rule.IsParsed)
        {
            yield return new ValidationIssue(IssueSeverity.Info, c.Message("unparsed", ("rule", raw)));
            yield break;
        }

        var tool = rule.Tool;
        if (PermissionRule.IsMcp(tool))
        {
            if (rule.Argument is not null)
            {
                yield return new ValidationIssue(IssueSeverity.Warning, c.Message("mcpParens", ("rule", raw)));
            }

            if (kind == "allow" && tool.Contains('*', StringComparison.Ordinal) && !IsAnchoredMcpGlob(tool))
            {
                yield return new ValidationIssue(IssueSeverity.Warning, c.Message("mcpAllowGlob", ("rule", raw)));
            }

            yield break;
        }

        if (kind == "allow" && tool.Contains('*', StringComparison.Ordinal))
        {
            yield return new ValidationIssue(IssueSeverity.Warning, c.Message("toolGlobAllow", ("rule", raw)));
        }

        if (c.Find(tool) is null)
        {
            if (tool != "*")
            {
                yield return new ValidationIssue(IssueSeverity.Info, c.Message("unknownTool", ("tool", tool), ("rule", raw)));
            }

            yield break;
        }

        if (PermissionRule.ParameterOf(tool, rule.Argument, c) is { } parameter)
        {
            if (parameter.IsExcluded)
            {
                yield return new ValidationIssue(IssueSeverity.Warning, c.Message("parameterExcluded", ("parameter", parameter.Name), ("rule", raw)));
            }
            else if (kind == "allow")
            {
                yield return new ValidationIssue(IssueSeverity.Warning, c.Message("parameterAllow", ("rule", raw)));
            }

            yield break;
        }

        if (rule.Argument?.Trim() is not { Length: > 0 } argument || c.ArgumentOf(tool) is not { } argumentKind)
        {
            yield break;
        }

        if (argumentKind.ArgumentWarning is { } warning)
        {
            yield return new ValidationIssue(IssueSeverity.Warning,
                warning.Replace("{rule}", raw, StringComparison.Ordinal).Replace("{tool}", tool, StringComparison.Ordinal));
        }

        if (argumentKind.Input == ArgumentInput.Prefixed && !argument.StartsWith(argumentKind.Prefix!, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationIssue(IssueSeverity.Warning, c.Message("domainPrefix", ("rule", raw)));
        }

        if (c.SeparatorsOf(tool) is { Count: > 0 } separators)
        {
            if (separators.Any(s => argument.Contains(s, StringComparison.Ordinal)))
            {
                yield return new ValidationIssue(IssueSeverity.Info, c.Message("compound", ("rule", raw)));
            }

            var colonStar = argument.IndexOf(":*", StringComparison.Ordinal);
            if (colonStar >= 0 && colonStar != argument.Length - 2)
            {
                yield return new ValidationIssue(IssueSeverity.Info, c.Message("colonStarMiddle", ("rule", raw)));
            }
        }

        if (c.KindKeyOf(tool) == "path" && WindowsPath().IsMatch(argument))
        {
            yield return new ValidationIssue(IssueSeverity.Warning, c.Message("windowsPath", ("rule", raw)));
        }
    }

    /// <summary>allow globs only work after a literal <c>mcp__server__</c> (so <c>mcp__*</c>, <c>mcp__gi*</c> are skipped).</summary>
    private static bool IsAnchoredMcpGlob(string tool)
    {
        var rest = tool[5..];
        var separator = rest.IndexOf("__", StringComparison.Ordinal);
        var star = rest.IndexOf('*', StringComparison.Ordinal);
        return separator > 0 && star > separator + 1;
    }

    // C:\x, C:/x, /C:/x
    [GeneratedRegex(@"^/?[A-Za-z]:[\\/]")]
    private static partial Regex WindowsPath();
}
