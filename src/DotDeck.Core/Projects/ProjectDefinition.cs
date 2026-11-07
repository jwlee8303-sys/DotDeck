using System.Text.Json.Nodes;

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

/// <summary>Explorer display filter (SPEC 2.3).</summary>
public enum ExplorerFilterMode
{
    /// <summary>Every file.</summary>
    All,

    /// <summary>Configuration and document files only.</summary>
    ConfigDocs,
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

    public ExplorerFilterMode Filter { get; init; }

    public int Order { get; init; }

    public bool Hidden { get; init; }

    /// <summary>Folder names excluded from the tree. Null means "use defaults".</summary>
    public IReadOnlyList<string>? ExcludedFolders { get; init; }

    /// <summary>"Show only these" relative paths chosen in visibility edit mode; null/empty shows everything.</summary>
    public IReadOnlyList<string>? VisibleItems { get; init; }

    public JsonObject? Extra { get; init; }

    public bool IsBuiltin => Kind == ProjectKind.Builtin;
}
