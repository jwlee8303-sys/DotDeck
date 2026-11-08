using DotDeck.Core.Catalogs;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Editors;

/// <summary>How the form edits a permission rule argument.</summary>
public enum ArgumentInput
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Free text after a fixed prefix (<see cref="ToolArgumentKind.Prefix"/>, WebFetch's <c>domain:</c>).</summary>
    Prefixed,

    /// <summary>Free text with suggestions (built-in <see cref="ToolArgumentKind.Choices"/> plus discovered names).</summary>
    Choice,

    /// <summary>The tool takes no argument; the box is off unless a rule already has one.</summary>
    Disabled,

    /// <summary>MCP tools: no argument, the scope (server / all tools / one tool) is picked instead.</summary>
    Mcp,
}

/// <summary>How a tool's permission rule argument is written (<c>tools.json</c> → <c>argumentKinds</c>).</summary>
public sealed class ToolArgumentKind
{
    /// <summary>Korean name of the kind, e.g. "명령".</summary>
    public string Label { get; init; } = string.Empty;

    public ArgumentInput Input { get; init; } = ArgumentInput.Text;

    /// <summary>Fixed text the argument starts with (<see cref="ArgumentInput.Prefixed"/>).</summary>
    public string? Prefix { get; init; }

    /// <summary>Documented names offered first (<see cref="ArgumentInput.Choice"/>).</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Compound command separators (command kinds); a tool can list its own.</summary>
    public IReadOnlyList<string> Separators { get; init; } = [];

    /// <summary>Warning for a rule of this kind that has an argument (none, notMatched); <c>{rule}</c>, <c>{tool}</c>.</summary>
    public string? ArgumentWarning { get; init; }

    public string Placeholder { get; init; } = string.Empty;

    /// <summary>Rules in Korean; <c>{slashBase}</c> is replaced with where <c>/path</c> points for the file's scope.</summary>
    public IReadOnlyList<string> Help { get; init; } = [];

    public IReadOnlyList<string> Examples { get; init; } = [];
}

public sealed class ToolInfo
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Listed first in pickers.</summary>
    public bool Common { get; init; }

    /// <summary>Asks for permission in the default mode.</summary>
    public bool Permission { get; init; }

    /// <summary>Key of <see cref="ClaudeToolCatalog.ArgumentKinds"/>: command, path, domain, agent, none, unverified, notMatched.</summary>
    public string Argument { get; init; } = "unverified";

    /// <summary>Other tools a rule for this tool also applies to.</summary>
    public string? AlsoCovers { get; init; }

    /// <summary>Top-level input parameters a deny/ask <c>Tool(name:value)</c> rule can match.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>Parameters Claude Code refuses in parameter rules (ignored with a warning).</summary>
    public IReadOnlyList<string> ExcludedParameters { get; init; } = [];

    /// <summary>Compound command separators for this tool (overrides the kind's).</summary>
    public IReadOnlyList<string>? Separators { get; init; }
}

public sealed class ParameterRuleHelp
{
    public IReadOnlyList<string> Help { get; init; } = [];

    public IReadOnlyList<string> Examples { get; init; } = [];
}

public sealed class McpHelp
{
    public IReadOnlyList<string> PermissionHelp { get; init; } = [];

    public IReadOnlyList<string> MatcherHelp { get; init; } = [];
}

/// <summary>Built-in Claude Code tools with Korean descriptions and permission rule syntax (<c>Catalogs/Claude/tools.json</c>).</summary>
public sealed class ClaudeToolCatalog
{
    public const string ClaudeResource = "Claude/tools.json";

    /// <summary>Kind key of every <c>mcp__</c> tool.</summary>
    public const string McpKind = "mcp";

    /// <summary>Message keys every catalog must have (permission rule warnings).</summary>
    public static readonly IReadOnlyList<string> MessageKeys =
    [
        "unparsed", "mcpParens", "mcpAllowGlob", "toolGlobAllow", "domainPrefix", "compound", "colonStarMiddle", "windowsPath",
        "parameterAllow", "parameterExcluded", "unknownTool", "duplicate", "conflict", "editDenyOnly",
    ];

    private static readonly Lazy<ClaudeToolCatalog> s_builtIn = new(LoadClaude);

    /// <summary>The catalog shipped with the app (used when no catalog is given).</summary>
    public static ClaudeToolCatalog BuiltIn => s_builtIn.Value;

    public int SchemaVersion { get; init; }

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyDictionary<string, ToolArgumentKind> ArgumentKinds { get; init; } = new Dictionary<string, ToolArgumentKind>();

    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    public McpHelp Mcp { get; init; } = new();

    public ParameterRuleHelp ParameterRules { get; init; } = new();

    /// <summary>Permission rule warnings by key (<see cref="MessageKeys"/>), with <c>{rule}</c>-style placeholders.</summary>
    public IReadOnlyDictionary<string, string> Messages { get; init; } = new Dictionary<string, string>();

    public static ClaudeToolCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<ClaudeToolCatalog>(json);
        if (catalog.SchemaVersion != 2 || catalog.Tools is null || catalog.ArgumentKinds is null || catalog.Mcp is null
            || catalog.Mcp.PermissionHelp is null || catalog.Mcp.MatcherHelp is null
            || catalog.ParameterRules is null || catalog.ParameterRules.Help is null || catalog.ParameterRules.Examples is null
            || catalog.Messages is null || MessageKeys.Any(k => !catalog.Messages.ContainsKey(k) || catalog.Messages[k] is null)
            || !catalog.ArgumentKinds.ContainsKey(McpKind)
            || catalog.Tools.Any(t => t is null || string.IsNullOrEmpty(t.Name) || t.Description is null || t.Argument is null
                || !catalog.ArgumentKinds.ContainsKey(t.Argument) || t.Parameters is null || t.ExcludedParameters is null)
            || catalog.ArgumentKinds.Values.Any(k => k is null || k.Help is null || k.Examples is null || k.Placeholder is null
                || k.Label is null || k.Choices is null || k.Separators is null
                || (k.Input == ArgumentInput.Prefixed && string.IsNullOrEmpty(k.Prefix))))
        {
            throw new InvalidDataException("Unsupported tools catalog.");
        }

        // Lists are used without further checks: a null item (or an empty separator, which matches everything) is invalid.
        var texts = catalog.ArgumentKinds.Values.SelectMany(k => k.Help.Concat(k.Examples).Concat(k.Choices))
            .Concat(catalog.Tools.SelectMany(t => t.Parameters.Concat(t.ExcludedParameters)))
            .Concat(catalog.ParameterRules.Help).Concat(catalog.ParameterRules.Examples)
            .Concat(catalog.Mcp.PermissionHelp).Concat(catalog.Mcp.MatcherHelp)
            .Concat(catalog.Messages.Values);
        var separators = catalog.ArgumentKinds.Values.SelectMany(k => k.Separators).Concat(catalog.Tools.SelectMany(t => t.Separators ?? []));
        if (texts.Any(t => t is null) || separators.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("Unsupported tools catalog.");
        }

        return catalog;
    }

    /// <summary>A message with its placeholders filled (<c>{rule}</c> → value).</summary>
    public string Message(string key, params (string Name, string Value)[] values)
    {
        var text = Messages.TryGetValue(key, out var found) ? found : key;
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }

    public static ClaudeToolCatalog LoadClaude() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource));

    /// <summary>Common tools first, then the rest, each in catalog order.</summary>
    public IEnumerable<ToolInfo> Ordered => Tools.Where(t => t.Common).Concat(Tools.Where(t => !t.Common));

    public ToolInfo? Find(string name) => Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>Kind key of <paramref name="tool"/>: <see cref="McpKind"/> for MCP tools, null for unknown tools.</summary>
    public string? KindKeyOf(string tool) => PermissionRule.IsMcp(tool) ? McpKind : Find(tool)?.Argument;

    public ToolArgumentKind? ArgumentOf(string tool) =>
        KindKeyOf(tool) is { } key && ArgumentKinds.TryGetValue(key, out var kind) ? kind : null;

    /// <summary>Compound command separators of a command tool (empty for other tools).</summary>
    public IReadOnlyList<string> SeparatorsOf(string tool) =>
        Find(tool)?.Separators is { } own ? own : ArgumentOf(tool)?.Separators ?? [];

    /// <summary>Where <c>/path</c> in a Read/Edit rule points for a settings file at <paramref name="role"/>.</summary>
    public static string SlashBase(FileRole? role) => role?.Scope switch
    {
        ConfigScope.Global => "사용자 설정에서는 ~/.claude/path",
        ConfigScope.Project or ConfigScope.Local => $"이 파일에서는 {role.BaseRoot}\\path",
        _ => "프로젝트·로컬 설정은 작업 폴더/path, 사용자 설정은 ~/.claude/path",
    };

    /// <summary>Help lines for a permission rule of <paramref name="tool"/> in a settings file at <paramref name="role"/>.</summary>
    public IReadOnlyList<string> PermissionHelp(string tool, FileRole? role)
    {
        if (PermissionRule.IsMcp(tool))
        {
            var mcp = ArgumentKinds[McpKind];
            return [.. mcp.Help, "예: " + string.Join("   ", mcp.Examples)];
        }

        var lines = new List<string>();
        if (Find(tool) is not { } info)
        {
            lines.Add($"{tool}: 목록에 없는 도구입니다. 이름을 그대로 저장합니다.");
            return lines;
        }

        lines.Add(info.Description + (info.Permission ? " (기본 모드에서 확인을 요청함)" : string.Empty));
        if (ArgumentOf(tool) is { } kind)
        {
            lines.Add($"인수 형식: {kind.Label}" + (kind.Placeholder.Length > 0 ? $" — {kind.Placeholder}" : string.Empty));
            lines.AddRange(kind.Help.Select(h => h.Replace("{slashBase}", SlashBase(role), StringComparison.Ordinal)));
            if (kind.Examples.Count > 0)
            {
                lines.Add("예: " + string.Join("   ", kind.Examples));
            }
        }

        if (info.AlsoCovers is { Length: > 0 } also)
        {
            lines.Add("함께 적용: " + also);
        }

        if (info.Parameters.Count > 0)
        {
            lines.Add($"매개변수 규칙(deny·ask): {tool}(매개변수:값) — 매개변수: {string.Join(", ", info.Parameters)}");
        }

        return lines;
    }

    /// <summary>Help lines for a parameter rule (<c>Tool(name:value)</c>).</summary>
    public IReadOnlyList<string> ParameterHelp() =>
        [.. ParameterRules.Help, "예: " + string.Join("   ", ParameterRules.Examples)];
}

public sealed class HookEventInfo
{
    public string Name { get; init; } = string.Empty;

    public bool Common { get; init; }

    public string When { get; init; } = string.Empty;

    /// <summary>tools (tool names), values (documented values or free text), none (matcher ignored).</summary>
    public string Matcher { get; init; } = "none";

    public string? MatcherTarget { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Only <c>|</c> separates matcher values (a comma takes the regex path).</summary>
    public bool PipeOnly { get; init; }

    /// <summary>The matcher also lists literal file names to watch, split on <c>|</c> (FileChanged).</summary>
    public bool FileNames { get; init; }

    public string Can { get; init; } = string.Empty;

    public string Example { get; init; } = string.Empty;

    public bool UsesMatcher => Matcher != "none";

    public bool MatchesTools => Matcher == "tools";

    public IReadOnlyList<string> HelpLines() =>
    [
        $"언제: {When}",
        UsesMatcher
            ? $"matcher: {(MatchesTools ? "도구 이름" : MatcherTarget ?? "값")}" + (Values is { Count: > 0 } v ? $" ({string.Join(", ", v)})" : string.Empty)
            : "matcher: 사용하지 않음(값이 있어도 무시됨)",
        $"할 수 있는 일: {Can}",
        $"사용 예: {Example}",
    ];
}

/// <summary>Claude Code hook events with Korean descriptions (<c>Catalogs/Claude/hook-events.json</c>).</summary>
public sealed class HookEventCatalog
{
    public const string ClaudeResource = "Claude/hook-events.json";

    private static readonly Lazy<HookEventCatalog> s_builtIn = new(() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource)));

    public int SchemaVersion { get; init; }

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyList<HookEventInfo> Events { get; init; } = [];

    public static HookEventCatalog BuiltIn => s_builtIn.Value;

    public static HookEventCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<HookEventCatalog>(json);
        if (catalog.SchemaVersion != 1 || catalog.Events is null
            || catalog.Events.Any(e => e is null || string.IsNullOrEmpty(e.Name) || e.When is null || e.Can is null || e.Example is null || e.Matcher is null))
        {
            throw new InvalidDataException("Unsupported hook-events catalog.");
        }

        return catalog;
    }

    public IEnumerable<HookEventInfo> Ordered => Events.Where(e => e.Common).Concat(Events.Where(e => !e.Common));

    public HookEventInfo? Find(string name) => Events.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
}
