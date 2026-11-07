using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

public enum ProjectRootState
{
    Ready,

    /// <summary>The root folder does not exist (UI shows "폴더 없음 — 만들기").</summary>
    Missing,

    /// <summary>The project has no folder (System project).</summary>
    Virtual,
}

/// <summary>Enumerates the nodes of one project's tree, already filtered and sorted for display.</summary>
public interface IProjectSource
{
    ProjectDefinition Project { get; }

    /// <summary>Absolute root folder; null for virtual projects.</summary>
    string? RootPath { get; }

    ProjectRootState GetRootState();

    /// <summary>
    /// Children of <paramref name="parent"/>, or of the project root when null. Folders first, then files,
    /// each by name. Throws <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> if the folder
    /// cannot be listed.
    /// </summary>
    IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent);
}

public static class ProjectSources
{
    public static IProjectSource Create(ProjectDefinition project, AppPaths paths, IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(paths);
        if (project.Builtin == BuiltinProject.System)
        {
            return new SystemProjectSource(project);
        }

        var root = project.Builtin is { } builtin ? BuiltinProjects.RootOf(builtin, paths) : project.Root;
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"Project '{project.Id}' has no root folder.", nameof(project));
        }

        return new FolderProjectSource(project, root, store);
    }
}
