using DotDeck.Core.Catalogs;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Editors;

/// <summary>How a tool's permission rule argument is written (<c>tools.json</c> → <c>argumentKinds</c>).</summary>
public sealed class ToolArgumentKind
{
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

    public int SchemaVersion { get; init; }

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyDictionary<string, ToolArgumentKind> ArgumentKinds { get; init; } = new Dictionary<string, ToolArgumentKind>();

    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    public McpHelp Mcp { get; init; } = new();

    public static ClaudeToolCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<ClaudeToolCatalog>(json);
        if (catalog.SchemaVersion != 1 || catalog.Tools is null || catalog.ArgumentKinds is null || catalog.Mcp is null
            || catalog.Mcp.PermissionHelp is null || catalog.Mcp.MatcherHelp is null
            || catalog.Tools.Any(t => t is null || string.IsNullOrEmpty(t.Name) || t.Description is null || t.Argument is null)
            || catalog.ArgumentKinds.Values.Any(k => k is null || k.Help is null || k.Examples is null || k.Placeholder is null))
        {
            throw new InvalidDataException("Unsupported tools catalog.");
        }

        return catalog;
    }

    public static ClaudeToolCatalog LoadClaude() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource));

    /// <summary>Common tools first, then the rest, each in catalog order.</summary>
    public IEnumerable<ToolInfo> Ordered => Tools.Where(t => t.Common).Concat(Tools.Where(t => !t.Common));

    public ToolInfo? Find(string name) => Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    public ToolArgumentKind? ArgumentOf(string tool) =>
        Find(tool) is { } info && ArgumentKinds.TryGetValue(info.Argument, out var kind) ? kind : null;

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
            return [.. Mcp.PermissionHelp];
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

        return lines;
    }
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
