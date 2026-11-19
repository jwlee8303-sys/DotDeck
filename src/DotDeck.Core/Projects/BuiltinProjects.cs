using DotDeck.Core.Paths;

namespace DotDeck.Core.Projects;

/// <summary>Fixed facts about the built-in projects (SPEC 2.1).</summary>
public static class BuiltinProjects
{
    public static IReadOnlyList<BuiltinProject> All { get; } = [BuiltinProject.Claude, BuiltinProject.Codex, BuiltinProject.Antigravity, BuiltinProject.System];

    public static string IdOf(BuiltinProject builtin) => builtin switch
    {
        BuiltinProject.Claude => "builtin.claude",
        BuiltinProject.Codex => "builtin.codex",
        BuiltinProject.Antigravity => "builtin.agy",
        BuiltinProject.System => "builtin.system",
        _ => throw new ArgumentOutOfRangeException(nameof(builtin)),
    };

    public static string NameOf(BuiltinProject builtin) => builtin switch
    {
        BuiltinProject.Claude => "Claude 전역",
        BuiltinProject.Codex => "Codex 전역",
        BuiltinProject.Antigravity => "agy 전역",
        BuiltinProject.System => "시스템",
        _ => throw new ArgumentOutOfRangeException(nameof(builtin)),
    };

    /// <summary>Key and id the Antigravity CLI (agy) project had while it was named "Gemini 전역" (≤ 1.10).</summary>
    public const string LegacyAntigravityKey = "gemini";

    public const string LegacyAntigravityId = "builtin.gemini";

    /// <summary>
    /// Maps a project id stored by an older version to the current one ("builtin.gemini" → "builtin.agy").
    /// Every reader of a persisted project id goes through this, so saved state follows the rename.
    /// </summary>
    public static string MigrateId(string id) =>
        string.Equals(id, LegacyAntigravityId, StringComparison.Ordinal) ? IdOf(BuiltinProject.Antigravity) : id;

    /// <summary>JSON key used in projects.json.</summary>
    public static string KeyOf(BuiltinProject builtin) => builtin switch
    {
        BuiltinProject.Claude => "claude",
        BuiltinProject.Codex => "codex",
        BuiltinProject.Antigravity => "agy",
        BuiltinProject.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(builtin)),
    };

    public static BuiltinProject? FromKey(string? key) => key?.ToLowerInvariant() switch
    {
        "claude" => BuiltinProject.Claude,
        "codex" => BuiltinProject.Codex,
        "agy" or LegacyAntigravityKey => BuiltinProject.Antigravity,
        "system" => BuiltinProject.System,
        _ => null,
    };

    /// <summary>Root folder of a built-in project, or null for the virtual System project.</summary>
    public static string? RootOf(BuiltinProject builtin, AppPaths paths) => builtin switch
    {
        BuiltinProject.Claude => paths.ClaudeHome,
        BuiltinProject.Codex => paths.CodexHome,
        BuiltinProject.Antigravity => paths.AntigravityHome,
        _ => null,
    };

    /// <summary>
    /// Large or noisy top-level folders hidden by default in built-in projects (decision Q5), in addition to the
    /// common defaults. Rooted ("/name") so that same-named folders deeper in the tree stay visible.
    /// Users can override by setting <see cref="ProjectDefinition.ExcludedFolders"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludedFoldersOf(BuiltinProject builtin) => builtin switch
    {
        BuiltinProject.Claude => ["/projects", "/todos", "/shell-snapshots", "/statsig", "/file-history", "/debug"],
        BuiltinProject.Codex => ["/sessions", "/log"],
        BuiltinProject.Antigravity => ["/tmp", "/history"],
        _ => [],
    };

    public static ProjectDefinition CreateDefault(BuiltinProject builtin, int order) => new()
    {
        Id = IdOf(builtin),
        Kind = ProjectKind.Builtin,
        Builtin = builtin,
        Name = NameOf(builtin),
        Order = order,
    };
}
