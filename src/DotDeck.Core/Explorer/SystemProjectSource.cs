using DotDeck.Core.Projects;

namespace DotDeck.Core.Explorer;

/// <summary>
/// The virtual System project (SPEC 2.1). In Phase 1 its entries are shown disabled (decision Q14);
/// environment variables and important files arrive in Phase 3.
/// </summary>
public sealed class SystemProjectSource : IProjectSource
{
    public const string UserEnvironmentId = "system:env-user";
    public const string MachineEnvironmentId = "system:env-machine";
    public const string PathId = "system:path";
    public const string ImportantFilesId = "system:important-files";

    private static readonly NodeInfo[] s_nodes =
    [
        Create(UserEnvironmentId, "환경변수(사용자)"),
        Create(MachineEnvironmentId, "환경변수(시스템)"),
        Create(PathId, "PATH"),
        Create(ImportantFilesId, "중요 파일"),
    ];

    public SystemProjectSource(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
    }

    public ProjectDefinition Project { get; }

    public string? RootPath => null;

    public ProjectRootState GetRootState() => ProjectRootState.Virtual;

    public IReadOnlyList<NodeInfo> GetChildren(NodeInfo? parent) => parent is null ? s_nodes : [];

    private static NodeInfo Create(string id, string name) => new()
    {
        Id = id,
        Kind = NodeKind.Virtual,
        Name = name,
        IsEnabled = false,
    };
}
