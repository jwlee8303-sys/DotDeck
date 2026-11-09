using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Explorer;

namespace DotDeck.Core.AiCli.Guard;

/// <summary>Scope data the guard needs for one decision (from scope.json).</summary>
public sealed record GuardPolicy
{
    /// <summary>Read/write roots (project roots of the session).</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Read-only roots (e.g. Claude's own skills/agents folders).</summary>
    public IReadOnlyList<string> ReadOnlyRoots { get; init; } = [];

    /// <summary>When true, reads outside the scope get no decision (SPEC A6 option "범위 밖 읽기 허용").</summary>
    public bool AllowOutsideReads { get; init; }

    /// <summary>Home folder for <c>~</c> in shell commands.</summary>
    public string? Home { get; init; }
}

/// <summary>
/// Pure decision function behind the PreToolUse scope guard (SPEC 11.7, DESIGN "범위 검사"). Inside the scope it
/// returns no decision (never allow), outside it denies, secret files are always denied, and shell commands whose
/// targets cannot be determined are asked.
/// </summary>
public sealed class GuardEvaluator(IFinalPathResolver resolver)
{
    /// <summary>Tool name prefix of DotDeck's own MCP server; it enforces the scope itself.</summary>
    public const string OwnMcpPrefix = "mcp__dotdeck_session__";

    private static readonly HashSet<string> NoPathTools = new(StringComparer.Ordinal)
    {
        "Agent", "Task", "TodoWrite", "TaskCreate", "TaskUpdate", "TaskList", "TaskGet", "TaskStop", "TaskOutput",
        "WebFetch", "WebSearch", "Skill", "AskUserQuestion", "ToolSearch", "EnterPlanMode", "ExitPlanMode",
        "SlashCommand", "BashOutput", "KillShell", "KillBash", "ListMcpResourcesTool", "ReadMcpResourceTool",
    };

    private readonly IFinalPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Evaluates the raw hook stdin JSON. Malformed input is denied.</summary>
    public GuardDecision EvaluateJson(string? hookInputJson, GuardPolicy policy)
    {
        JsonObject? input;
        try
        {
            input = JsonNode.Parse(hookInputJson ?? string.Empty) as JsonObject;
        }
        catch (JsonException)
        {
            input = null;
        }

        return input is null
            ? GuardDecision.Deny("DotDeck 범위 가드: 훅 입력을 해석할 수 없습니다")
            : Evaluate(input, policy);
    }

    public GuardDecision Evaluate(JsonObject input, GuardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);

        var toolName = GetString(input, "tool_name");
        if (string.IsNullOrEmpty(toolName))
        {
            return GuardDecision.Deny("DotDeck 범위 가드: 도구 이름이 없습니다");
        }

        var toolInput = input["tool_input"] as JsonObject ?? [];
        var cwd = GetString(input, "cwd");
        if (cwd is not null && !Path.IsPathFullyQualified(cwd))
        {
            cwd = null;
        }

        var roots = new List<ScopeRoot>(policy.Roots.Select(r => new ScopeRoot(r)));
        roots.AddRange(policy.ReadOnlyRoots.Select(r => new ScopeRoot(r, ReadOnly: true)));
        if (GetString(input, "scratchpad_dir") is { } scratchpad && Path.IsPathFullyQualified(scratchpad))
        {
            roots.Add(new ScopeRoot(scratchpad));
        }

        var checker = new ScopeChecker(roots, _resolver);
        var context = new Context(checker, cwd, policy);

        switch (toolName)
        {
            case "Write" or "Edit" or "MultiEdit":
                return CheckFile(context, GetString(toolInput, "file_path"), ScopeAccess.Write, required: true);
            case "NotebookEdit":
                return CheckFile(context, GetString(toolInput, "notebook_path") ?? GetString(toolInput, "file_path"), ScopeAccess.Write, required: true);
            case "Read":
                return CheckFile(context, GetString(toolInput, "file_path"), ScopeAccess.Read, required: true);
            case "Glob":
                return CheckSearch(context, GetString(toolInput, "path"), GetString(toolInput, "pattern"));
            case "Grep":
                return CheckSearch(context, GetString(toolInput, "path"), GetString(toolInput, "glob"));
            case "Bash":
                return CheckShell(context, GetString(toolInput, "command"), ShellKind.Bash);
            case "PowerShell":
                return CheckShell(context, GetString(toolInput, "command"), ShellKind.PowerShell);
        }

        if (toolName.StartsWith(OwnMcpPrefix, StringComparison.Ordinal))
        {
            return GuardDecision.NoDecision;
        }

        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return GuardDecision.Ask($"DotDeck 범위 가드: 외부 MCP 도구({toolName})는 대상 경로를 확인할 수 없습니다");
        }

        if (NoPathTools.Contains(toolName))
        {
            return GuardDecision.NoDecision;
        }

        // Unknown tool: check every field that is named like a path.
        var decision = GuardDecision.NoDecision;
        foreach (var (key, value) in toolInput)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsPathKey(key))
            {
                decision = decision.Max(CheckFile(context, text, ScopeAccess.Write, required: false));
            }
        }

        return decision;
    }

    private sealed record Context(ScopeChecker Checker, string? Cwd, GuardPolicy Policy);

    private static GuardDecision CheckFile(Context context, string? path, ScopeAccess access, bool required)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return required ? GuardDecision.Deny("DotDeck 범위 가드: 대상 경로가 없습니다") : GuardDecision.NoDecision;
        }

        var decision = context.Checker.Check(path, context.Cwd, access, context.Policy.Home);
        if (decision.Outcome == ScopeOutcome.Inside)
        {
            return IsSecret(path, decision.FinalPath)
                ? GuardDecision.Deny($"DotDeck 범위 가드: 비밀 파일은 AI 세션에서 열 수 없습니다 ({Path.GetFileName(decision.FinalPath ?? path)})")
                : GuardDecision.NoDecision;
        }

        if (access == ScopeAccess.Read && context.Policy.AllowOutsideReads && decision.Outcome == ScopeOutcome.Outside)
        {
            return IsSecret(path, decision.FinalPath)
                ? GuardDecision.Deny("DotDeck 범위 가드: 비밀 파일은 AI 세션에서 열 수 없습니다")
                : GuardDecision.NoDecision;
        }

        return GuardDecision.Deny("DotDeck 범위 가드: " + decision.Reason);
    }

    private static GuardDecision CheckSearch(Context context, string? path, string? pattern)
    {
        var decision = GuardDecision.NoDecision;
        var basePath = string.IsNullOrWhiteSpace(path) ? context.Cwd : path;
        if (basePath is null)
        {
            return GuardDecision.Deny("DotDeck 범위 가드: 검색 위치를 확인할 수 없습니다");
        }

        decision = decision.Max(CheckFile(context, basePath, ScopeAccess.Read, required: true));

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var normalized = pattern.Replace('/', '\\');
            if (Path.IsPathRooted(normalized) || normalized.StartsWith(@"..\", StringComparison.Ordinal) || normalized.Contains(@"\..\", StringComparison.Ordinal))
            {
                // Anchor the pattern at the search folder resolved against cwd (a relative "path" counts too).
                var anchor = PathNormalizer.Normalize(basePath, context.Cwd, context.Policy.Home);
                var prefix = ShellCommandAnalyzer.FixedPrefix(normalized);
                string? target = Path.IsPathRooted(prefix) ? prefix
                    : anchor.Kind == NormalizedPathKind.Path ? Path.Join(anchor.FullPath, prefix)
                    : null;
                decision = target is null
                    ? decision.Max(GuardDecision.Deny("DotDeck 범위 가드: 검색 위치를 확인할 수 없습니다"))
                    : decision.Max(CheckFile(context, target, ScopeAccess.Read, required: true));
            }

            if (TargetsSecretFiles(normalized))
            {
                decision = decision.Max(GuardDecision.Deny("DotDeck 범위 가드: 비밀 파일을 찾는 검색은 허용되지 않습니다"));
            }
        }

        return decision;
    }

    /// <summary>Representative secret names (SPEC 2.3) and ordinary names used to tell targeted patterns from broad ones.</summary>
    private static readonly string[] SecretSamples = [".env", ".env.local", ".env.production", "id_rsa", "id_rsa.pub", "id_ed25519", "auth.json", "server.pem", "server.key", "cert.pfx"];

    private static readonly string[] OrdinarySamples = ["readme.md", "notes.txt", "settings.json", "package.json", "program.cs", "config.yml", "app.config"];

    /// <summary>
    /// True when the last segment of a glob is aimed at secret files: it matches a secret name but none of the ordinary
    /// names (so <c>*</c> or <c>*.json</c> stay allowed while <c>auth.*</c>, <c>.env*</c> or <c>*.pem</c> are denied).
    /// Broad patterns are covered by the session's <c>permissions.deny</c> secret rules instead.
    /// </summary>
    internal static bool TargetsSecretFiles(string pattern)
    {
        var last = pattern.Replace('/', '\\').Split('\\').LastOrDefault() ?? string.Empty;
        if (last.Length == 0)
        {
            return false;
        }

        foreach (var alternative in ExpandBraces(last))
        {
            var hitsSecret = SecretSamples.Any(s => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(alternative, s, ignoreCase: true));
            var hitsOrdinary = OrdinarySamples.Any(s => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(alternative, s, ignoreCase: true));
            if (hitsSecret && !hitsOrdinary)
            {
                return true;
            }

            if (!alternative.Contains('*', StringComparison.Ordinal) && !alternative.Contains('?', StringComparison.Ordinal)
                && SecretFilePolicy.IsSecret(alternative))
            {
                return true;
            }

            // Sample names cannot cover every secret variant ("foo*.pem", ".env.staging*"): also try the concrete names
            // the pattern produces when each wildcard matches nothing or a single character.
            if (WildcardInstances(alternative).Any(SecretFilePolicy.IsSecret))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> WildcardInstances(string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return [];
        }

        string[] empty = [pattern.Replace("*", string.Empty, StringComparison.Ordinal).Replace('?', 'x')];
        string[] single = [pattern.Replace('*', 'x').Replace('?', 'x')];
        return empty.Concat(single).Where(n => n.Length > 0);
    }

    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        var close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (open < 0 || close < 0)
        {
            return [pattern];
        }

        var head = pattern[..open];
        var tail = pattern[(close + 1)..];
        return pattern[(open + 1)..close].Split(',').SelectMany(option => ExpandBraces(head + option + tail));
    }

    private static GuardDecision CheckShell(Context context, string? command, ShellKind kind)
    {
        var analysis = ShellCommandAnalyzer.Analyze(command, kind);
        var decision = GuardDecision.NoDecision;

        foreach (var word in analysis.BareWords)
        {
            if (SecretFilePolicy.IsSecret(word) || TargetsSecretFiles(word))
            {
                return GuardDecision.Deny($"DotDeck 범위 가드: 비밀 파일은 AI 세션에서 다룰 수 없습니다 ({word})");
            }

            // A plain name may still be a link to somewhere else (or to a secret file): resolve it against cwd. Names
            // that cannot be resolved (command words, messages) are not path evidence and give no decision.
            var check = context.Checker.Check(ShellCommandAnalyzer.FixedPrefix(word), context.Cwd, ScopeAccess.Write, context.Policy.Home);
            if (check.Outcome == ScopeOutcome.Outside)
            {
                return GuardDecision.Deny("DotDeck 범위 가드: " + check.Reason);
            }

            if (check.Outcome == ScopeOutcome.Inside && IsSecret(word, check.FinalPath))
            {
                return GuardDecision.Deny("DotDeck 범위 가드: 비밀 파일은 AI 세션에서 다룰 수 없습니다");
            }
        }

        foreach (var candidate in analysis.PathCandidates)
        {
            var path = candidate;
            if (kind == ShellKind.Bash && path.StartsWith('/'))
            {
                var converted = ShellCommandAnalyzer.FromPosix(path);
                if (converted is null)
                {
                    decision = decision.Max(GuardDecision.Ask($"DotDeck 범위 가드: 셸 경로({path})의 실제 위치를 확인할 수 없습니다"));
                    continue;
                }

                path = converted;
            }

            if (TargetsSecretFiles(path))
            {
                return GuardDecision.Deny($"DotDeck 범위 가드: 비밀 파일은 AI 세션에서 다룰 수 없습니다 ({candidate})");
            }

            path = ShellCommandAnalyzer.FixedPrefix(path);
            var check = context.Checker.Check(path, context.Cwd, ScopeAccess.Write, context.Policy.Home);
            switch (check.Outcome)
            {
                case ScopeOutcome.Outside:
                    return GuardDecision.Deny("DotDeck 범위 가드: " + check.Reason);
                case ScopeOutcome.Unresolved:
                    decision = decision.Max(GuardDecision.Ask($"DotDeck 범위 가드: 셸 경로({candidate})의 실제 위치를 확인할 수 없습니다"));
                    break;
                case ScopeOutcome.Inside when IsSecret(path, check.FinalPath):
                    return GuardDecision.Deny("DotDeck 범위 가드: 비밀 파일은 AI 세션에서 다룰 수 없습니다");
            }
        }

        if (analysis.AskReason is not null)
        {
            decision = decision.Max(GuardDecision.Ask("DotDeck 범위 가드: " + analysis.AskReason));
        }

        return decision;
    }

    /// <summary>Secret by the name as written or by the final target's name (links count as their target).</summary>
    private static bool IsSecret(string path, string? finalPath)
    {
        var name = Path.GetFileName(PathNormalizer.Unquote(path).TrimEnd('\\', '/'));
        if (name.Length > 0 && SecretFilePolicy.IsSecret(name))
        {
            return true;
        }

        return finalPath is not null && SecretFilePolicy.IsSecret(Path.GetFileName(finalPath));
    }

    private static bool IsPathKey(string key) =>
        key.Contains("path", StringComparison.OrdinalIgnoreCase)
        || key.Contains("file", StringComparison.OrdinalIgnoreCase)
        || key.Contains("dir", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
