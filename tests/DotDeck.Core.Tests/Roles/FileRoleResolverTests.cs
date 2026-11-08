using DotDeck.Core.Catalogs;
using DotDeck.Core.Editors;
using DotDeck.Core.Files;
using DotDeck.Core.Roles;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Roles;

public sealed class FileRoleResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _home;
    private readonly string _project;

    public FileRoleResolverTests()
    {
        _home = _temp.Combine("home");
        _project = _temp.Combine("work", "web");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
    }

    public void Dispose() => _temp.Dispose();

    private FileRoleResolver Resolver(string? home = null, IReadOnlyList<string>? roots = null, bool managed = false) =>
        new([FileRoleCatalog.LoadClaude()], PhysicalFileStore.Instance, home ?? _home, () => roots ?? [_project], managed);

    [Theory]
    [InlineData("settings.json", "settings", EditorKind.ClaudeSettings, ConfigScope.Global)]
    [InlineData("settings.local.json", "settings", EditorKind.ClaudeSettings, ConfigScope.Local)]
    [InlineData(@"agents\reviewer.md", "agent", EditorKind.Agent, ConfigScope.Global)]
    [InlineData(@"agents\team\reviewer.md", "agent", EditorKind.Agent, ConfigScope.Global)]
    [InlineData(@"skills\pdf\SKILL.md", "skill", EditorKind.Skill, ConfigScope.Global)]
    [InlineData(@"skills\synced\pdf\SKILL.md", "skill", EditorKind.Skill, ConfigScope.Global)]
    [InlineData(@"commands\deploy.md", "command", EditorKind.Markdown, ConfigScope.Global)]
    [InlineData(@"rules\api\style.md", "rule", EditorKind.Markdown, ConfigScope.Global)]
    [InlineData("CLAUDE.md", "memory", EditorKind.Markdown, ConfigScope.Global)]
    [InlineData("keybindings.json", "keybindings", EditorKind.Text, ConfigScope.Global)]
    [InlineData(@"projects\D--p\memory\MEMORY.md", "autoMemory", EditorKind.Markdown, ConfigScope.Global)]
    public void Global_claude_folder_files(string relative, string role, EditorKind editor, ConfigScope scope)
    {
        var result = Resolver().Resolve(Path.Combine(_home, ".claude", relative));

        Assert.NotNull(result);
        Assert.Equal(role, result.RoleId);
        Assert.Equal(editor, result.Editor);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(Path.Combine(_home, ".claude"), result.ConfigDir);
        Assert.Equal(_home, result.BaseRoot);
    }

    [Theory]
    [InlineData("settings.json", "settings", EditorKind.ClaudeSettings, ConfigScope.Project)]
    [InlineData("settings.local.json", "settings", EditorKind.ClaudeSettings, ConfigScope.Local)]
    [InlineData(@"agents\reviewer.md", "agent", EditorKind.Agent, ConfigScope.Project)]
    [InlineData(@"skills\deploy\SKILL.md", "skill", EditorKind.Skill, ConfigScope.Project)]
    [InlineData("CLAUDE.md", "memory", EditorKind.Markdown, ConfigScope.Project)]
    public void Project_claude_folder_files(string relative, string role, EditorKind editor, ConfigScope scope)
    {
        var result = Resolver().Resolve(Path.Combine(_project, ".claude", relative));

        Assert.NotNull(result);
        Assert.Equal(role, result.RoleId);
        Assert.Equal(editor, result.Editor);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(_project, result.BaseRoot);
        Assert.Equal(_project, result.ProjectRoot);
    }

    [Theory]
    [InlineData(@"src\settings.json")]
    [InlineData(@"docs\agents\a.md")]
    [InlineData(@"skills\pdf\SKILL.md")]
    [InlineData(@"tools\skills\pdf\SKILL.md")]
    [InlineData(@"src\settings.local.json")]
    [InlineData(@".claude\hooks\guard.py")]
    [InlineData(@".claude\skills\pdf\scripts\run.md")]
    [InlineData(@".claude\keybindings.json")]
    public void Same_names_outside_their_place_are_not_claude_files(string relative)
    {
        Assert.Null(Resolver().Resolve(Path.Combine(_project, relative)));
    }

    [Fact]
    public void Nearest_claude_folder_wins()
    {
        var nested = Path.Combine(_project, "apps", "admin");
        Directory.CreateDirectory(Path.Combine(nested, ".claude"));

        var result = Resolver().Resolve(Path.Combine(nested, ".claude", "skills", "x", "SKILL.md"));

        Assert.NotNull(result);
        Assert.Equal(nested, result.BaseRoot);
        Assert.Equal(ConfigScope.Project, result.Scope);
        Assert.Contains(result.Notes, n => n.Contains("하위 폴더의 .claude", StringComparison.Ordinal));

        // A .claude folder inside another one is still the nearest.
        var inner = Resolver().Resolve(Path.Combine(_project, ".claude", "x", ".claude", "settings.json"));
        Assert.Equal(Path.Combine(_project, ".claude", "x"), inner!.BaseRoot);
    }

    [Fact]
    public void Home_override_moves_the_global_level()
    {
        var file = Path.Combine(_project, ".claude", "settings.json");

        Assert.Equal(ConfigScope.Project, Resolver().Resolve(file)!.Scope);
        // With the project folder as the home, its .claude is the global one.
        Assert.Equal(ConfigScope.Global, Resolver(home: _project).Resolve(file)!.Scope);
        Assert.Equal(ConfigScope.Project, Resolver(home: _project).Resolve(Path.Combine(_home, ".claude", "settings.json"))!.Scope);
    }

    [Fact]
    public void Files_outside_claude_folders_use_project_roots()
    {
        var resolver = Resolver();

        Assert.Equal("memory", resolver.Resolve(Path.Combine(_project, "CLAUDE.md"))!.RoleId);
        Assert.Equal(ConfigScope.Project, resolver.Resolve(Path.Combine(_project, "CLAUDE.md"))!.Scope);
        Assert.Equal(ConfigScope.Local, resolver.Resolve(Path.Combine(_project, "CLAUDE.local.md"))!.Scope);
        Assert.Equal(ConfigScope.Subdirectory, resolver.Resolve(Path.Combine(_project, "src", "CLAUDE.md"))!.Scope);
        Assert.Equal("mcp", resolver.Resolve(Path.Combine(_project, ".mcp.json"))!.RoleId);
        Assert.Null(resolver.Resolve(Path.Combine(_project, "src", ".mcp.json")));
        Assert.Equal("appState", resolver.Resolve(Path.Combine(_home, ".claude.json"))!.RoleId);
        Assert.Null(resolver.Resolve(Path.Combine(_project, ".claude.json")));
    }

    [Fact]
    public void Git_folder_marks_an_unregistered_project_root()
    {
        var repo = _temp.Combine("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var result = Resolver(roots: []).Resolve(Path.Combine(repo, ".mcp.json"));

        Assert.Equal(ConfigScope.Project, result!.Scope);
        Assert.Equal(repo, result.BaseRoot);
    }

    [Fact]
    public void Managed_folder_only_when_enabled()
    {
        var path = @"C:\Program Files\ClaudeCode\managed-settings.json";

        Assert.Null(Resolver(managed: false).Resolve(path));
        var role = Resolver(managed: true).Resolve(path);
        Assert.Equal(ConfigScope.Managed, role!.Scope);
        Assert.Equal(EditorKind.ClaudeSettings, role.Editor);
    }

    [Fact]
    public void Paths_are_compared_without_case()
    {
        var upper = Path.Combine(_home.ToUpperInvariant(), ".CLAUDE", "AGENTS", "X.MD");
        Assert.Equal(ConfigScope.Global, Resolver().Resolve(upper)!.Scope);
    }

    [Fact]
    public void Summary_shows_name_and_scope()
    {
        var path = Path.Combine(_project, ".claude", "settings.local.json");
        Assert.Equal("settings.local.json · 로컬(Git 제외)", Resolver().Resolve(path)!.Summary(path));
    }

    [Fact]
    public void Default_editor_without_a_role()
    {
        var resolver = Resolver();
        Assert.Equal(EditorKind.Markdown, resolver.DefaultEditor(@"C:\x\a.md", null));
        Assert.Equal(EditorKind.Text, resolver.DefaultEditor(@"C:\x\a.json", null));
    }

    [Fact]
    public void Catalog_records_sources_and_rejects_bad_files()
    {
        var catalog = FileRoleCatalog.LoadClaude();
        Assert.NotEmpty(catalog.Sources);
        Assert.All(catalog.Sources, s => Assert.StartsWith("https://code.claude.com/docs/", s.Url, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => FileRoleCatalog.Parse("{\"schemaVersion\":2,\"product\":\"x\",\"configDirName\":\".x\"}"));
    }

    [Fact]
    public void Catalog_override_file_wins_and_a_broken_one_falls_back()
    {
        var dir = _temp.Combine("catalogs");
        Directory.CreateDirectory(Path.Combine(dir, "Claude"));
        var file = Path.Combine(dir, "Claude", "file-roles.json");
        File.WriteAllText(file, "{\"schemaVersion\":1,\"product\":\"claude\",\"configDirName\":\".claude\",\"rules\":[]}");

        var loaded = CatalogResources.Load(FileRoleCatalog.ClaudeResource, FileRoleCatalog.Parse, PhysicalFileStore.Instance, dir);
        Assert.Equal(CatalogOrigin.Override, loaded.Origin);
        Assert.Empty(loaded.Value.Rules);

        File.WriteAllText(file, "{ broken");
        var fallback = CatalogResources.Load(FileRoleCatalog.ClaudeResource, FileRoleCatalog.Parse, PhysicalFileStore.Instance, dir);
        Assert.Equal(CatalogOrigin.BuiltIn, fallback.Origin);
        Assert.NotNull(fallback.Warning);
        Assert.NotEmpty(fallback.Value.Rules);
    }
}
