using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class ProjectSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ProjectDefinition Folder(string root, IReadOnlyList<string>? excluded = null) => new()
    {
        Id = "p1",
        Kind = ProjectKind.Folder,
        Name = "P",
        Root = root,
        ExcludedFolders = excluded,
    };

    private void Touch(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void Children_are_folders_first_then_files_by_name()
    {
        Touch("b.md");
        Touch("A.md");
        Touch("zeta", "f.md");
        Touch("Alpha", "f.md");
        var source = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);

        var names = source.GetChildren(null).Select(n => n.Name);

        Assert.Equal(["Alpha", "zeta", "A.md", "b.md"], names);
    }

    [Fact]
    public void Default_extensions_hide_other_files_and_folders_without_visible_files()
    {
        Touch("CLAUDE.md");
        Touch("photo.png");
        Touch("assets", "logo.png");
        var all = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);
        var source = new VisibleProjectSource(all, PhysicalFileStore.Instance,
            new VisibilityEvaluator(VisibilitySettings.Default, ExtensionRules.ConfigDocsPreset), new ProjectViewSettings { Mode = ExplorerViewMode.Tree });

        var root = source.GetChildren(null);

        // Spec change (SPEC 2.3): a folder with nothing to show is hidden (it used to stay visible and empty).
        Assert.Equal(["CLAUDE.md"], root.Select(n => n.Name));
        Assert.Equal(["assets", "CLAUDE.md", "photo.png"], all.GetChildren(null).Select(n => n.Name));
    }

    [Fact]
    public void Former_default_excluded_folders_are_listed_at_every_depth()
    {
        Touch("node_modules", "x.js");
        Touch("src", "Bin", "x.dll");
        Touch("src", "keep", "x.md");
        Touch(".git", "config");
        var source = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);

        var root = source.GetChildren(null);
        Assert.Equal([".git", "node_modules", "src"], root.Select(n => n.Name));
        Assert.Equal(["Bin", "keep"], source.GetChildren(root[2]).Select(n => n.Name));
    }

    [Fact]
    public void Legacy_project_exclusions_do_not_hide_folders()
    {
        Touch("node_modules", "x.js");
        Touch("gen", "a.md");
        Touch("src", "gen", "b.md");
        var source = new FolderProjectSource(Folder(_temp.Path, excluded: ["/gen"]), _temp.Path, PhysicalFileStore.Instance);

        var root = source.GetChildren(null);
        Assert.Equal(["gen", "node_modules", "src"], root.Select(n => n.Name));
        Assert.Equal(["gen"], source.GetChildren(root[2]).Select(n => n.Name));
    }

    [Fact]
    public void Builtin_projects_no_longer_apply_folder_exclusions()
    {
        var project = BuiltinProjects.CreateDefault(BuiltinProject.Claude, 0);
        var policy = ExcludedFolderPolicy.For(project);

        Assert.False(policy.IsExcluded("projects"));
        Assert.False(policy.IsExcluded("DEBUG"));
        Assert.False(policy.IsExcluded(@"skills\node_modules"));
        Assert.False(policy.IsExcluded(@"skills\x\debug"));
        Assert.False(policy.IsExcluded("skills"));
        Assert.Empty(ExcludedFolderPolicy.DefaultsFor(project));
    }

    [Theory]
    [InlineData("src/gen", @"src\gen", true)]
    [InlineData(@"\src\gen\", @"src\gen", true)]
    [InlineData("src/gen", @"other\src\gen", false)]
    [InlineData(" cache ", @"a\Cache", true)]
    [InlineData("debug/", @"skills\debug", true)]
    [InlineData(@"debug\", "debug", true)]
    [InlineData("/debug", @"skills\debug", false)]
    public void Excluded_entries_are_normalized(string entry, string relative, bool expected)
    {
        Assert.Equal(expected, new ExcludedFolderPolicy([entry, "", "   "]).IsExcluded(relative));
    }

    [Fact]
    public void Nodes_carry_kind_secret_and_relative_path()
    {
        Touch("sub", ".env");
        Touch("sub", "settings.json");
        var source = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);
        var sub = source.GetChildren(null).Single();

        var children = source.GetChildren(sub);

        var env = children.Single(n => n.Name == ".env");
        Assert.True(env.IsSecret);
        Assert.True(env.CanOpen);
        Assert.Equal(Path.Combine("sub", ".env"), env.RelativePath);
        var json = children.Single(n => n.Name == "settings.json");
        Assert.Equal(FileKind.Json, json.FileKind);
        Assert.True(json.CanOpen);
        Assert.Equal(json.FullPath, json.Id);
        Assert.True(sub.CanExpand);
        Assert.Empty(source.GetChildren(json));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Access_denied_folder_throws_instead_of_looking_empty()
    {
        Touch("locked", "a.md");
        var locked = new DirectoryInfo(_temp.Combine("locked"));
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(rule);
        locked.SetAccessControl(security);
        try
        {
            var source = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);
            var node = source.GetChildren(null).Single();

            Assert.Throws<UnauthorizedAccessException>(() => source.GetChildren(node));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void Missing_root_is_reported()
    {
        var root = _temp.Combine("missing");
        var source = new FolderProjectSource(Folder(root), root, PhysicalFileStore.Instance);

        Assert.Equal(ProjectRootState.Missing, source.GetRootState());
    }

    [Fact]
    public void Factory_resolves_builtin_roots_from_home_and_system_is_virtual()
    {
        var paths = new AppPaths(_temp.Path, HomeSource.CommandLine, _temp.Combine("roaming"), _temp.Combine("local"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".claude"));

        var claude = ProjectSources.Create(BuiltinProjects.CreateDefault(BuiltinProject.Claude, 0), paths, PhysicalFileStore.Instance);
        var codex = ProjectSources.Create(BuiltinProjects.CreateDefault(BuiltinProject.Codex, 1), paths, PhysicalFileStore.Instance);
        var system = ProjectSources.Create(BuiltinProjects.CreateDefault(BuiltinProject.System, 2), paths, PhysicalFileStore.Instance);

        Assert.Equal(Path.Combine(_temp.Path, ".claude"), claude.RootPath);
        Assert.Equal(ProjectRootState.Ready, claude.GetRootState());
        Assert.Equal(ProjectRootState.Missing, codex.GetRootState());
        Assert.Equal(ProjectRootState.Virtual, system.GetRootState());
        Assert.Null(system.RootPath);
        var nodes = system.GetChildren(null);
        Assert.Equal(4, nodes.Count);
        Assert.All(nodes, n => Assert.True(n.IsEnabled));
        Assert.All(nodes, n => Assert.False(n.CanOpen));
        Assert.Equal(NodeKind.Folder, nodes[3].Kind);
    }
}
