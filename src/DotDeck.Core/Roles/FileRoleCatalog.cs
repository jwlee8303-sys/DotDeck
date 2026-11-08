using System.Text.RegularExpressions;
using DotDeck.Core.Catalogs;
using DotDeck.Core.Editors;

namespace DotDeck.Core.Roles;

/// <summary>Where a rule's <see cref="FileRoleRule.Pattern"/> is matched from.</summary>
public enum RuleBase
{
    /// <summary>Relative to the nearest configuration folder (e.g. <c>.claude</c>) above the file.</summary>
    ConfigDir,

    /// <summary>Relative to the home folder.</summary>
    Home,

    /// <summary>The file name, in a folder that is a project root (has <c>.claude</c> or <c>.git</c>, or is registered).</summary>
    AnchorDir,

    /// <summary>The file name, in any folder outside configuration folders.</summary>
    AnyDir,

    /// <summary>Relative to the organization's managed settings folder.</summary>
    ManagedDir,
}

/// <summary>How a rule decides the scope.</summary>
public enum RuleScope
{
    /// <summary>Global when the configuration folder sits directly in the home folder, project otherwise.</summary>
    Auto,
    Global,
    Project,
    Local,
    Subdirectory,
    Managed,
}

public sealed class FileRoleRule
{
    public string Id { get; init; } = string.Empty;

    public RuleBase Base { get; init; }

    public string Pattern { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    /// <summary>text, markdown, agentForm, skillForm, settingsForm. Unknown names open as text.</summary>
    public string Editor { get; init; } = "text";

    public RuleScope Scope { get; init; }

    /// <summary>"global" or "project": the rule applies only to configuration folders at that level.</summary>
    public string? OnlyIn { get; init; }

    public IReadOnlyList<string>? Notes { get; init; }

    public IReadOnlyList<string>? GlobalNotes { get; init; }

    public IReadOnlyList<string>? ProjectNotes { get; init; }

    internal Regex? Compiled { get; set; }
}

/// <summary>
/// Location rules of one product (<c>Catalogs/&lt;Product&gt;/file-roles.json</c>). Rules are tried in order; the
/// first match wins. Patterns use <c>/</c> separators, <c>*</c> (within a segment) and <c>**</c> (any segments),
/// compared without case.
/// </summary>
public sealed class FileRoleCatalog
{
    public const string ClaudeResource = "Claude/file-roles.json";

    public int SchemaVersion { get; init; }

    public string Product { get; init; } = string.Empty;

    public string ProductLabel { get; init; } = string.Empty;

    public string ConfigDirName { get; init; } = string.Empty;

    public string? ManagedDir { get; init; }

    public string? NestedConfigDirNote { get; init; }

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<FileRoleRule> Rules { get; init; } = [];

    public static FileRoleCatalog Parse(string json)
    {
        var catalog = CatalogResources.Deserialize<FileRoleCatalog>(json);
        // JSON null overrides the defaults of init properties: reject it here instead of failing later.
        if (catalog.SchemaVersion != 1 || string.IsNullOrEmpty(catalog.Product) || string.IsNullOrEmpty(catalog.ConfigDirName)
            || catalog.Rules is null || catalog.Roles is null || catalog.Sources is null)
        {
            throw new InvalidDataException("Unsupported file-roles catalog.");
        }

        foreach (var rule in catalog.Rules)
        {
            if (rule is null || string.IsNullOrEmpty(rule.Pattern) || string.IsNullOrEmpty(rule.Role) || rule.Editor is null
                || rule.Notes?.Any(n => n is null) == true || rule.GlobalNotes?.Any(n => n is null) == true
                || rule.ProjectNotes?.Any(n => n is null) == true)
            {
                throw new InvalidDataException($"Rule '{rule?.Id}' has a missing pattern, role or note.");
            }

            rule.Compiled = CompilePattern(rule.Pattern);
        }

        return catalog;
    }

    public static FileRoleCatalog LoadClaude() => Parse(CatalogResources.ReadBuiltIn(ClaudeResource));

    public string RoleLabel(string role) => Roles.TryGetValue(role, out var label) ? label : role;

    public static EditorKind EditorOf(string? name) => name switch
    {
        "settingsForm" => EditorKind.ClaudeSettings,
        "agentForm" => EditorKind.Agent,
        "skillForm" => EditorKind.Skill,
        "markdown" => EditorKind.Markdown,
        _ => EditorKind.Text,
    };

    /// <summary>Glob to an anchored, case-insensitive regex over a <c>/</c>-separated relative path.</summary>
    internal static Regex CompilePattern(string pattern)
    {
        var parts = pattern.Split('/');
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (parts[i] == "**")
            {
                // Zero or more whole segments.
                sb.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }

            foreach (var c in parts[i])
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }

            if (!last)
            {
                sb.Append('/');
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
