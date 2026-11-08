using DotDeck.Core.Editors;

namespace DotDeck.Core.Roles;

/// <summary>Which settings level a configuration file belongs to.</summary>
public enum ConfigScope
{
    /// <summary>The user's own level (<c>~/.claude</c>), applies to every project.</summary>
    Global,

    /// <summary>A project's shared level (committed to the repository).</summary>
    Project,

    /// <summary>A project's personal level (kept out of Git).</summary>
    Local,

    /// <summary>A folder below the working folder; read only when Claude works on files there.</summary>
    Subdirectory,

    /// <summary>Deployed by the organization.</summary>
    Managed,
}

/// <summary>
/// What a file is to a tool such as Claude Code, decided from where it lives (DESIGN "파일 역할 판별").
/// </summary>
/// <param name="Product">Catalog product id, e.g. <c>claude</c>.</param>
/// <param name="RoleId">Catalog role id, e.g. <c>settings</c>, <c>agent</c>.</param>
/// <param name="ConfigDir">The <c>.claude</c> folder the file was matched under, or null for files outside one.</param>
/// <param name="BaseRoot">
/// The folder the file's relative paths refer to: the home folder for global files, the folder that holds
/// <c>.claude</c> (the project root) for project files, the file's folder for other files.
/// </param>
/// <param name="RuleId">The catalog rule that matched.</param>
public sealed record FileRole(
    string Product,
    string RoleId,
    string RoleLabel,
    ConfigScope Scope,
    string? ConfigDir,
    string BaseRoot,
    EditorKind Editor,
    string RuleId,
    IReadOnlyList<string> Notes)
{
    public string ScopeLabel => LabelOf(Scope);

    /// <summary>The project root for project-level files; null for global, managed and subfolder files.</summary>
    public string? ProjectRoot => Scope is ConfigScope.Project or ConfigScope.Local ? BaseRoot : null;

    public static string LabelOf(ConfigScope scope) => scope switch
    {
        ConfigScope.Global => "전역",
        ConfigScope.Project => "프로젝트",
        ConfigScope.Local => "로컬(Git 제외)",
        ConfigScope.Subdirectory => "하위 폴더",
        ConfigScope.Managed => "관리형",
        _ => scope.ToString(),
    };

    /// <summary>e.g. <c>settings.json · 프로젝트</c>.</summary>
    public string Summary(string path) => $"{Path.GetFileName(path)} · {ScopeLabel}";
}
