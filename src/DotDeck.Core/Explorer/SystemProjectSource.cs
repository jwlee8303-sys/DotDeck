using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.SystemConfig;

namespace DotDeck.Core.Explorer;

/// <summary>
/// The virtual System project (SPEC 2.1, §8): environment variable tables, the PATH editor and the important
/// files list. Environment nodes open system documents; important files are ordinary file nodes.
/// </summary>
public sealed class SystemProjectSource : IProjectSource
{
    public const string UserEnvironmentId = "system:env-user";
    public const string MachineEnvironmentId = "system:env-machine";
    public const string PathId = "system:path";
    public const string ImportantFilesId = "system:important-files";

    private readonly AppPaths _paths;
    private readonly IFileStore _store;

    public SystemProjectSource(ProjectDefinition project, AppPaths paths, IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
        _paths = paths;
        _store = store;
    }

    public ProjectDefinition Project { get; }

    public string? RootPath => null;

    public ProjectRootState GetRootState() => ProjectRootState.Virtual;

    public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent)
    {
        if (parent is null)
        {
            return
            [
                Create(UserEnvironmentId, "환경변수(사용자)"),
                Create(MachineEnvironmentId, "환경변수(시스템)"),
                Create(PathId, "PATH"),
                new NodeInfo { Id = ImportantFilesId, Kind = NodeKind.Folder, Name = CustomFileRules.NodeName },
            ];
        }

        if (parent.Id != ImportantFilesId)
        {
            return [];
        }

        var list = new ImportantFiles(_store, ImportantFiles.ListFileFor(_paths));
        return CustomFileRules.CreateNodes(_store, list.List(ImportantFiles.DefaultsFor(_paths)));
    }

    private static NodeInfo Create(string id, string name) => new()
    {
        Id = id,
        Kind = NodeKind.Virtual,
        Name = name,
    };
}
