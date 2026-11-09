using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class SessionScopeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;

    public SessionScopeTests()
    {
        _paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("roaming"), _temp.Combine("local"));
        Directory.CreateDirectory(_paths.ClaudeHome);
        Directory.CreateDirectory(_paths.CodexHome);
    }

    public void Dispose() => _temp.Dispose();

    private string MakeDir(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectDefinition Folder(string id, string name, string? root) =>
        new() { Id = id, Kind = ProjectKind.Folder, Name = name, Root = root };

    private ScopeEligibility Evaluate(ProjectDefinition project) =>
        ScopeEligibility.Evaluate(project, _paths, PhysicalFileStore.Instance);

    [Fact]
    public void Folder_project_with_existing_root_is_eligible_and_selected_by_default()
    {
        var root = MakeDir("웹 게임");

        var result = Evaluate(Folder("p1", "웹게임", root));

        Assert.Equal(ScopeEligibilityStatus.Eligible, result.Status);
        Assert.True(result.IsSelectable);
        Assert.True(result.IsSelectedByDefault);
        Assert.Equal(root, result.Root);
    }

    [Theory]
    [InlineData(BuiltinProject.Claude)]
    [InlineData(BuiltinProject.Codex)]
    public void Global_projects_are_selectable_with_warning_but_not_selected_by_default(BuiltinProject builtin)
    {
        var result = Evaluate(BuiltinProjects.CreateDefault(builtin, 0));

        Assert.Equal(ScopeEligibilityStatus.EligibleGlobalSettings, result.Status);
        Assert.True(result.IsSelectable);
        Assert.False(result.IsSelectedByDefault);
        Assert.Equal(ScopeEligibility.GlobalSettingsWarning, result.Reason);
    }

    [Fact]
    public void System_project_is_never_selectable()
    {
        var result = Evaluate(BuiltinProjects.CreateDefault(BuiltinProject.System, 0));

        Assert.Equal(ScopeEligibilityStatus.NotSelectable, result.Status);
        Assert.False(result.IsSelectable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\path")]
    public void Project_without_absolute_root_is_disabled(string? root)
    {
        var result = Evaluate(Folder("p1", "virtual", root));

        Assert.Equal(ScopeEligibilityStatus.Disabled, result.Status);
        Assert.False(result.IsSelectable);
    }

    [Fact]
    public void Project_whose_folder_is_missing_is_disabled()
    {
        var result = Evaluate(Folder("p1", "gone", _temp.Combine("missing")));

        Assert.Equal(ScopeEligibilityStatus.Disabled, result.Status);
        Assert.Equal("폴더 없음", result.Reason);
    }

    [Fact]
    public void Missing_global_folder_is_disabled()
    {
        Directory.Delete(_paths.CodexHome);

        var result = Evaluate(BuiltinProjects.CreateDefault(BuiltinProject.Codex, 0));

        Assert.Equal(ScopeEligibilityStatus.Disabled, result.Status);
    }

    [Fact]
    public void Create_builds_primary_and_additional_and_skips_invalid_entries()
    {
        var a = MakeDir("DotDeck");
        var b = MakeDir("웹 게임");
        ProjectDefinition[] projects =
        [
            Folder("a", "DotDeck", a),
            Folder("b", "웹게임", b),
            Folder("gone", "Gone", _temp.Combine("nope")),
            BuiltinProjects.CreateDefault(BuiltinProject.System, 2),
        ];

        var result = SessionScope.Create("a", ["b", "a", "b", "gone", "builtin.system", "unknown"], projects, _paths, PhysicalFileStore.Instance);

        Assert.Null(result.Error);
        var scope = Assert.IsType<SessionScope>(result.Scope);
        Assert.Equal("a", scope.Primary.ProjectId);
        Assert.Equal(["b"], scope.Additional.Select(p => p.ProjectId));
        Assert.Equal([a, b], scope.Roots);
        Assert.Equal(["gone", "builtin.system", "unknown"], result.Skipped.Select(s => s.ProjectId));
        Assert.False(scope.IncludesGlobalProject);
    }

    [Fact]
    public void Create_fails_when_primary_is_not_selectable()
    {
        ProjectDefinition[] projects = [BuiltinProjects.CreateDefault(BuiltinProject.System, 0)];

        var result = SessionScope.Create("builtin.system", [], projects, _paths, PhysicalFileStore.Instance);

        Assert.Null(result.Scope);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Create_fails_when_primary_is_unknown()
    {
        var result = SessionScope.Create("x", [], [], _paths, PhysicalFileStore.Instance);

        Assert.Null(result.Scope);
        Assert.Contains("x", result.Error);
    }

    [Fact]
    public void Global_project_in_scope_is_flagged()
    {
        var a = MakeDir("DotDeck");
        ProjectDefinition[] projects = [Folder("a", "DotDeck", a), BuiltinProjects.CreateDefault(BuiltinProject.Claude, 0)];

        var scope = SessionScope.Create("a", ["builtin.claude"], projects, _paths, PhysicalFileStore.Instance).Scope!;

        Assert.True(scope.IncludesGlobalProject);
        Assert.Equal([a, _paths.ClaudeHome], scope.Roots);
    }

    [Fact]
    public void Exact_duplicate_roots_are_dropped_but_nested_roots_are_kept()
    {
        var parent = MakeDir("work");
        var child = MakeDir("work", "child");
        var sibling = MakeDir("work2");
        ProjectDefinition[] projects =
        [
            Folder("p", "Parent", parent),
            Folder("c", "Child", child),
            Folder("dup", "Dup", parent + Path.DirectorySeparatorChar),
            Folder("dup2", "Dup2", sibling.ToUpperInvariant()),
            Folder("s", "Sibling", sibling),
        ];

        var scope = SessionScope.Create("p", ["c", "dup", "s", "dup2"], projects, _paths, PhysicalFileStore.Instance).Scope!;

        // A nested folder may be a junction to another place, so it is passed on; only the scope checker decides.
        Assert.Equal([child, sibling], scope.AdditionalDirectories);
        Assert.Equal([parent, child, sibling], scope.Roots);
    }

    [Fact]
    public void Nested_junction_root_stays_an_additional_directory()
    {
        var primary = MakeDir("a");
        var target = MakeDir("elsewhere", "b");
        var junction = Path.Combine(primary, "link");
        if (!TryCreateJunction(junction, target))
        {
            Assert.Skip("junction creation unavailable");
        }

        ProjectDefinition[] projects = [Folder("a", "A", primary), Folder("b", "B", junction)];

        var scope = SessionScope.Create("a", ["b"], projects, _paths, PhysicalFileStore.Instance).Scope!;

        Assert.Equal([junction], scope.AdditionalDirectories);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Without_removes_additional_project_and_refuses_primary()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        ProjectDefinition[] projects = [Folder("a", "A", a), Folder("b", "B", b)];
        var scope = SessionScope.Create("a", ["b"], projects, _paths, PhysicalFileStore.Instance).Scope!;

        var smaller = scope.Without("b");

        Assert.NotNull(smaller);
        Assert.Empty(smaller.Additional);
        Assert.Equal([a], smaller.Roots);
        Assert.Equal(ScopeKey.Of("a", []), smaller.Key);
        Assert.Null(scope.Without("a"));
    }

    [Fact]
    public void Title_lists_additional_projects()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        ProjectDefinition[] projects = [Folder("a", "DotDeck", a), Folder("b", "웹게임", b)];

        var scope = SessionScope.Create("a", ["b"], projects, _paths, PhysicalFileStore.Instance).Scope!;

        Assert.Equal("Claude · DotDeck (+웹게임)", scope.Title("Claude"));
        Assert.Equal("Claude · DotDeck", scope.Without("b")!.Title("Claude"));
    }

    [Fact]
    public void Scope_key_ignores_order_duplicates_and_primary_repetition()
    {
        Assert.Equal(ScopeKey.Of("a", ["b", "c"]), ScopeKey.Of("a", ["c", "b", "c", "a"]));
        Assert.Equal("[\"a\"]", ScopeKey.Of("a", []));
        Assert.NotEqual(ScopeKey.Of("a", ["b"]), ScopeKey.Of("b", ["a"]));
    }

    [Fact]
    public void Scope_key_does_not_collide_for_ids_with_separators()
    {
        Assert.NotEqual(ScopeKey.Of("a", ["b|c"]), ScopeKey.Of("a", ["b", "c"]));
        Assert.NotEqual(ScopeKey.Of("a|b", []), ScopeKey.Of("a", ["b"]));
    }
}
