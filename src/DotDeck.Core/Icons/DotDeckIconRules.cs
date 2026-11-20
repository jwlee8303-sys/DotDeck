using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Icons;

/// <summary>One tool-scoped rule: applies only to items below that tool's configuration root.</summary>
/// <param name="Pattern">Glob over <see cref="IconScope.RelativePath"/> ("settings.json", "agents", "**/mcp_config.json").</param>
public sealed record ScopedIconRule(IconTool Tool, string Pattern, bool IsFolder, string Icon)
{
    internal Regex Compiled { get; init; } = FileRoleCatalog.CompilePattern(Pattern);
}

/// <summary>
/// DotDeck's own mapping (<c>Assets/FileIcons/dotdeck-icons.json</c>, shipped with the app), between the user's
/// associations and Material. Distinctive names (CLAUDE.md, AGENTS.md, .claude) apply everywhere; common names
/// (settings.json, agents, config) only through <see cref="Scoped"/> rules.
/// <code>
/// { "fileNames": { "AGENTS.md": "dotdeck-shared-doc" }, "folderNames": { ".claude": "dotdeck-folder-claude" },
///   "scoped": [ { "tool": "claude", "match": "agents", "folder": true, "icon": "dotdeck-folder-claude" } ] }
/// </code>
/// </summary>
public sealed record DotDeckIconRules
{
    private static readonly HashSet<string> s_sections = ["fileNames", "folderNames", "scoped"];

    public static DotDeckIconRules Empty { get; } = new();

    public IReadOnlyDictionary<string, string> FileNames { get; init; } = IconMappings.EmptyMap;

    public IReadOnlyDictionary<string, string> FolderNames { get; init; } = IconMappings.EmptyMap;

    public IReadOnlyList<ScopedIconRule> Scoped { get; init; } = [];

    /// <summary>An immutable copy with case-insensitive keys.</summary>
    internal DotDeckIconRules Snapshot() => new()
    {
        FileNames = IconJson.Freeze(FileNames),
        FolderNames = IconJson.Freeze(FolderNames),
        Scoped = Scoped.ToArray(),
    };

    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static DotDeckIconRules Parse(string json, IReadOnlySet<string> availableKeys, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(availableKeys);
        ArgumentNullException.ThrowIfNull(warnings);
        using var doc = JsonDocument.Parse(json, IconJson.Options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("dotdeck-icons.json must contain a JSON object.");
        }

        IconJson.WarnDuplicates(root, warnings);
        foreach (var p in root.EnumerateObject())
        {
            if (!IconJson.TryName(p, out var section) || (!s_sections.Contains(section) && !section.StartsWith('$')))
            {
                warnings.Add($"알 수 없는 항목 '{IconJson.Short(section)}'을 무시합니다.");
            }
        }

        IReadOnlyDictionary<string, string> Names(string name)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IconJson.TryFirst(root, name, out var obj))
            {
                return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }

            if (obj.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"'{name}'은 객체여야 합니다. 무시합니다.");
                return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var p in obj.EnumerateObject())
            {
                if (IconJson.TryName(p, out var key) && IconJson.TryString(p.Value, out var icon) && availableKeys.Contains(icon))
                {
                    map[key] = icon;
                }
                else
                {
                    warnings.Add($"DotDeck 아이콘 매핑 '{name}.{IconJson.Short(key)}'의 아이콘이 없어 무시합니다.");
                }
            }

            return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        var scoped = new List<ScopedIconRule>();
        var hasList = IconJson.TryFirst(root, "scoped", out var list);
        if (hasList && list.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("'scoped'는 배열이어야 합니다. 무시합니다.");
        }
        else if (hasList)
        {
            var index = -1;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                string? Text(string n) => IconJson.TryFirst(item, n, out var v) && IconJson.TryString(v, out var t) ? t : null;
                var tool = Text("tool") switch
                {
                    "claude" => IconTool.Claude,
                    "codex" => IconTool.Codex,
                    "antigravity" => IconTool.Antigravity,
                    "shared" => IconTool.Shared,
                    _ => (IconTool?)null,
                };
                var match = Text("match")?.Trim('/');
                var icon = Text("icon");
                var folderKind = IconJson.TryFirst(item, "folder", out var f) ? f.ValueKind : JsonValueKind.False;
                if (tool is null || string.IsNullOrEmpty(match) || icon is null || !availableKeys.Contains(icon)
                    || folderKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    warnings.Add($"DotDeck 아이콘 규칙 scoped[{index}]이 올바르지 않아 무시합니다(tool·match·icon 필수, folder는 true/false).");
                    continue;
                }

                var folder = folderKind == JsonValueKind.True;

                scoped.Add(new ScopedIconRule(tool.Value, match, folder, icon));
            }
        }

        return new DotDeckIconRules { FileNames = Names("fileNames"), FolderNames = Names("folderNames"), Scoped = scoped };
    }

    internal string? MatchScoped(IconScope scope, bool isFolder)
    {
        foreach (var rule in Scoped)
        {
            if (rule.Tool != scope.Tool || rule.IsFolder != isFolder)
            {
                continue;
            }

            try
            {
                if (rule.Compiled.IsMatch(scope.RelativePath))
                {
                    return rule.Icon;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological path must not break the tree: this rule just does not match.
            }
        }

        return null;
    }
}
