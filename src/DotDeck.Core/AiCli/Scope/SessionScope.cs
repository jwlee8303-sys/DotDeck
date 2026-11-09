using System.Text.Json;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.Core.AiCli.Scope;

/// <summary>One project inside a session scope with its resolved root folder.</summary>
public sealed record ScopeProject(string ProjectId, string Name, string Root, bool IsGlobal);

/// <summary>
/// The folders an AI CLI session may work in (SPEC 11.2): the primary project is the CLI working directory and the
/// additional projects become extra working directories. The scope is the union of their roots.
/// </summary>
public sealed record SessionScope
{
    private SessionScope(ScopeProject primary, IReadOnlyList<ScopeProject> additional)
    {
        Primary = primary;
        Additional = additional;
        Key = ScopeKey.Of(primary.ProjectId, additional.Select(p => p.ProjectId));
    }

    public ScopeProject Primary { get; }

    public IReadOnlyList<ScopeProject> Additional { get; }

    /// <summary>Stable identity of the project selection (independent of names and roots).</summary>
    public string Key { get; }

    public IEnumerable<ScopeProject> Projects => [Primary, .. Additional];

    public bool IncludesGlobalProject => Projects.Any(p => p.IsGlobal);

    /// <summary>
    /// Additional working directories for the CLI: additional roots in selection order without exact duplicates of
    /// each other or of the primary root. Lexically nested roots are kept on purpose: a nested folder may be a
    /// junction to somewhere else, and only the scope checker (which resolves final paths) can tell.
    /// </summary>
    public IReadOnlyList<string> AdditionalDirectories =>
        [.. Additional.Select(p => p.Root)
            .Where(r => !string.Equals(r, Primary.Root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>All roots of the scope (primary first), without nested duplicates.</summary>
    public IReadOnlyList<string> Roots => [Primary.Root, .. AdditionalDirectories];

    /// <summary>
    /// Builds a scope from project ids. Fails when the primary project is missing or not selectable; additional
    /// projects that are missing, not selectable, duplicated or equal to the primary are skipped and reported.
    /// </summary>
    public static SessionScopeResult Create(
        string primaryProjectId,
        IEnumerable<string> additionalProjectIds,
        IReadOnlyList<ProjectDefinition> projects,
        AppPaths paths,
        IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(primaryProjectId);
        ArgumentNullException.ThrowIfNull(additionalProjectIds);
        ArgumentNullException.ThrowIfNull(projects);

        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var skipped = new List<SkippedProject>();

        if (!byId.TryGetValue(primaryProjectId, out var primaryDefinition))
        {
            return SessionScopeResult.Failed($"주 프로젝트를 찾을 수 없습니다: {primaryProjectId}");
        }

        var primaryEligibility = ScopeEligibility.Evaluate(primaryDefinition, paths, store);
        if (!primaryEligibility.IsSelectable)
        {
            return SessionScopeResult.Failed($"{primaryDefinition.Name}: {primaryEligibility.Reason}");
        }

        var primary = ToScopeProject(primaryDefinition, primaryEligibility);
        var seen = new HashSet<string>(StringComparer.Ordinal) { primaryProjectId };
        var additional = new List<ScopeProject>();
        foreach (var id in additionalProjectIds)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var definition))
            {
                skipped.Add(new(id, null, "프로젝트를 찾을 수 없습니다"));
                continue;
            }

            var eligibility = ScopeEligibility.Evaluate(definition, paths, store);
            if (!eligibility.IsSelectable)
            {
                skipped.Add(new(id, definition.Name, eligibility.Reason ?? "선택할 수 없습니다"));
                continue;
            }

            additional.Add(ToScopeProject(definition, eligibility));
        }

        return new SessionScopeResult(new SessionScope(primary, additional), skipped, null);
    }

    /// <summary>The scope without one additional project, or null when that would remove the primary project.</summary>
    public SessionScope? Without(string projectId)
    {
        if (string.Equals(Primary.ProjectId, projectId, StringComparison.Ordinal))
        {
            return null;
        }

        return new SessionScope(Primary, [.. Additional.Where(p => !string.Equals(p.ProjectId, projectId, StringComparison.Ordinal))]);
    }

    /// <summary>Tab title such as <c>Claude · DotDeck (+웹게임)</c> (SPEC 11.5 A).</summary>
    public string Title(string cliName)
    {
        var title = $"{cliName} · {Primary.Name}";
        return Additional.Count == 0 ? title : $"{title} (+{string.Join(", ", Additional.Select(p => p.Name))})";
    }

    private static ScopeProject ToScopeProject(ProjectDefinition definition, ScopeEligibility eligibility) =>
        new(definition.Id, definition.Name, eligibility.Root!, eligibility.Status == ScopeEligibilityStatus.EligibleGlobalSettings);
}

public sealed record SkippedProject(string ProjectId, string? Name, string Reason);

public sealed record SessionScopeResult(SessionScope? Scope, IReadOnlyList<SkippedProject> Skipped, string? Error)
{
    public static SessionScopeResult Failed(string error) => new(null, [], error);
}

/// <summary>
/// Identity of a project selection: a JSON array of the primary id followed by the sorted, distinct additional ids.
/// JSON encoding keeps ids containing separators from colliding.
/// </summary>
public static class ScopeKey
{
    public static string Of(string primaryProjectId, IEnumerable<string> additionalProjectIds)
    {
        var additional = additionalProjectIds
            .Where(id => !string.Equals(id, primaryProjectId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return JsonSerializer.Serialize<string[]>([primaryProjectId, .. additional]);
    }
}
