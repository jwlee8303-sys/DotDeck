using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.Core.AiCli.Scope;

/// <summary>Whether a project can be part of an AI CLI session scope (SPEC 11.2).</summary>
public enum ScopeEligibilityStatus
{
    /// <summary>Selectable without warnings.</summary>
    Eligible,

    /// <summary>Selectable, but the CLI may change global Claude/Codex settings (shown as a warning).</summary>
    EligibleGlobalSettings,

    /// <summary>The System project (environment variables, PATH, custom files) can never be selected.</summary>
    NotSelectable,

    /// <summary>The project has no root folder or the folder does not exist; shown disabled.</summary>
    Disabled,
}

public sealed record ScopeEligibility(ScopeEligibilityStatus Status, string? Root, string? Reason)
{
    public bool IsSelectable => Status is ScopeEligibilityStatus.Eligible or ScopeEligibilityStatus.EligibleGlobalSettings;

    /// <summary>Global projects are excluded unless the user opts in.</summary>
    public bool IsSelectedByDefault => Status == ScopeEligibilityStatus.Eligible;

    public const string GlobalSettingsWarning = "전역 설정이 수정될 수 있음";

    /// <summary>Applies the SPEC 11.2 rules to one project.</summary>
    public static ScopeEligibility Evaluate(ProjectDefinition project, AppPaths paths, IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);

        if (project.Builtin == BuiltinProject.System)
        {
            return new(ScopeEligibilityStatus.NotSelectable, null, "시스템 프로젝트는 선택할 수 없습니다");
        }

        var root = project.IsBuiltin && project.Builtin is { } builtin
            ? BuiltinProjects.RootOf(builtin, paths)
            : project.Root;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return new(ScopeEligibilityStatus.Disabled, null, "루트 폴더가 없는 프로젝트입니다");
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!store.DirectoryExists(root))
        {
            return new(ScopeEligibilityStatus.Disabled, root, "폴더 없음");
        }

        return project.IsBuiltin
            ? new(ScopeEligibilityStatus.EligibleGlobalSettings, root, GlobalSettingsWarning)
            : new(ScopeEligibilityStatus.Eligible, root, null);
    }
}
