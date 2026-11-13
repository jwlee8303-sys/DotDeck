using System.Text.Json.Nodes;
using DotDeck.Core.Explorer.Visibility;

namespace DotDeck.Core.Projects;

public enum ProjectKind
{
    Builtin,
    Folder,
}

public enum BuiltinProject
{
    Claude,
    Codex,
    System,
}

/// <summary>
/// One registered project. Built-in projects have no stored root (it is derived from the home path)
/// and cannot be removed or renamed. <see cref="Extra"/> carries JSON fields this version does not know,
/// so saving never drops data written by a newer version.
/// </summary>
public sealed record ProjectDefinition
{
    public required string Id { get; init; }

    public required ProjectKind Kind { get; init; }

    public BuiltinProject? Builtin { get; init; }

    /// <summary>Display name. For built-in projects this is the fixed localized name.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute root folder for <see cref="ProjectKind.Folder"/> projects; null for built-ins.</summary>
    public string? Root { get; init; }

    /// <summary>Optional accent color (#RRGGBB) shown next to the project header.</summary>
    public string? Color { get; init; }

    public int Order { get; init; }

    public bool Hidden { get; init; }

    /// <summary>Legacy field retained for file compatibility; project folders are no longer excluded.</summary>
    public IReadOnlyList<string>? ExcludedFolders { get; init; }

    /// <summary>What the explorer shows (rules, default extensions). Ignored for the System project.</summary>
    public VisibilitySettings Visibility { get; init; } = VisibilitySettings.Default;

    /// <summary>Normal-mode layout and pinned items.</summary>
    public ProjectViewSettings View { get; init; } = ProjectViewSettings.Default;

    /// <summary>
    /// Files registered under the project's "사용자 지정 파일" node (absolute paths, anywhere on disk). Not used by
    /// the System project, which keeps its own list (<c>important-files.json</c>).
    /// </summary>
    public IReadOnlyList<string> CustomFiles { get; init; } = [];

    public JsonObject? Extra { get; init; }

    public bool IsBuiltin => Kind == ProjectKind.Builtin;
}
