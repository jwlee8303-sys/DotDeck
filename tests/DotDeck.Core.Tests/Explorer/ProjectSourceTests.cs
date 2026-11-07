using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class ProjectSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ProjectDefinition Folder(string root, ExplorerFilterMode filter = ExplorerFilterMode.All,
        IReadOnlyList<string>? excluded = null) => new()
    {
        Id = "p1",
        Kind = ProjectKind.Folder,
        Name = "P",
        Root = root,
        Filter = filter,
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
    public void ConfigDocs_filter_hides_files_but_keeps_folders()
    {
        Touch("CLAUDE.md");
        Touch("photo.png");
        Touch("assets", "logo.png");
        var source = new FolderProjectSource(Folder(_temp.Path, ExplorerFilterMode.ConfigDocs), _temp.Path, PhysicalFileStore.Instance);

        var root = source.GetChildren(null);

        Assert.Equal(["assets", "CLAUDE.md"], root.Select(n => n.Name));
        Assert.Empty(source.GetChildren(root[0]));
    }

    [Fact]
    public void Default_excluded_folders_are_hidden_at_any_depth()
    {
        Touch("node_modules", "x.js");
        Touch("src", "Bin", "x.dll");
        Touch("src", "keep", "x.md");
        Touch(".git", "config");
        var source = new FolderProjectSource(Folder(_temp.Path), _temp.Path, PhysicalFileStore.Instance);

        var root = source.GetChildren(null);
        Assert.Equal(["src"], root.Select(n => n.Name));
        Assert.Equal(["keep"], source.GetChildren(root[0]).Select(n => n.Name));
    }

    [Fact]
    public void Project_override_replaces_defaults_and_supports_rooted_entries()
    {
        Touch("node_modules", "x.js");
        Touch("gen", "a.md");
        Touch("src", "gen", "b.md");
        var source = new FolderProjectSource(Folder(_temp.Path, excluded: ["/gen"]), _temp.Path, PhysicalFileStore.Instance);

        var root = source.GetChildren(null);
        Assert.Equal(["node_modules", "src"], root.Select(n => n.Name));
        Assert.Equal(["gen"], source.GetChildren(root[1]).Select(n => n.Name));
    }

    [Fact]
    public void Builtin_rooted_defaults_only_hide_top_level_folders()
    {
        var project = BuiltinProjects.CreateDefault(BuiltinProject.Claude, 0);
        var policy = ExcludedFolderPolicy.For(project);

        Assert.True(policy.IsExcluded("projects"));
        Assert.True(policy.IsExcluded("DEBUG"));
        Assert.True(policy.IsExcluded(@"skills\node_modules"));
        Assert.False(policy.IsExcluded(@"skills\x\debug"));
        Assert.False(policy.IsExcluded("skills"));
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
        Assert.False(env.CanOpen);
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
        Assert.All(nodes, n => Assert.False(n.IsEnabled));
        Assert.All(nodes, n => Assert.False(n.CanOpen));
    }
}
