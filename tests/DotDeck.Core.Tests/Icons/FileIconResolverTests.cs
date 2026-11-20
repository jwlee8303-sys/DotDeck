using DotDeck.Core.Icons;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Icons;

public sealed class FileIconResolverTests
{
    private const string Material = """
        {
          "iconDefinitions": {
            "file": {}, "folder": {}, "folder-open": {}, "markdown": {}, "typescript": {}, "typescript-def": {}, "test-ts": {},
            "docker": {}, "git": {}, "settings": {}, "folder-src": {}, "folder-src-open": {}, "folder-config": {}, "folder-config-open": {},
            "folder-docs": {}, "folder-docs-open": {}, "folder-robot": {}, "json": {}, "json_light": {}, "html": {}, "powershell": {}, "toml": {}
          },
          "file": "file", "folder": "folder", "folderExpanded": "folder-open",
          "fileExtensions": { "md": "markdown", "ts": "typescript", "d.ts": "typescript-def", "spec.ts": "test-ts", "json": "json", "toml": "toml", "bad": "missing" },
          "fileNames": { "dockerfile": "docker", ".gitignore": "git", "settings.json": "settings" },
          "folderNames": { "src": "folder-src", "config": "folder-config", "docs": "folder-docs", "agents": "folder-robot" },
          "folderNamesExpanded": { "src": "folder-src-open", "config": "folder-config-open", "docs": "folder-docs-open" },
          "languageIds": { "html": "html", "powershell": "powershell" },
          "light": { "fileExtensions": { "json": "json_light" } }
        }
        """;

    private const string DotDeck = """
        {
          "fileNames": { "AGENTS.md": "dotdeck-shared-doc", "CLAUDE.md": "dotdeck-claude-doc" },
          "folderNames": { ".claude": "dotdeck-folder-claude" },
          "scoped": [
            { "tool": "claude", "match": "settings.json", "icon": "dotdeck-claude-settings" },
            { "tool": "claude", "match": "agents", "folder": true, "icon": "dotdeck-folder-claude" },
            { "tool": "antigravity", "match": "config", "folder": true, "icon": "dotdeck-folder-agy" },
            { "tool": "antigravity", "match": "**/mcp_config.json", "icon": "dotdeck-agy-mcp" }
          ]
        }
        """;

    private static readonly HashSet<string> s_keys =
    [
        "file", "folder", "folder-open", "markdown", "typescript", "typescript-def", "test-ts", "docker", "git", "settings",
        "folder-src", "folder-src-open", "folder-config", "folder-config-open", "folder-docs", "folder-docs-open", "folder-robot",
        "json", "json_light", "html", "powershell", "toml",
        "dotdeck-shared-doc", "dotdeck-claude-doc", "dotdeck-folder-claude", "dotdeck-folder-claude-open", "dotdeck-claude-settings",
        "dotdeck-folder-agy", "dotdeck-agy-mcp",
    ];

    private readonly ListAppLog _log = new();

    private FileIconResolver Create(string? user = null, string? dotDeck = DotDeck, string? material = Material) =>
        FileIconResolver.Create(new IconSources(material, dotDeck, user, s_keys), _log);

    private IEnumerable<string> Warnings => _log.Entries.Where(e => e.Level == AppLogLevel.Warning).Select(e => e.Message);

    private IEnumerable<string> UserWarnings => Warnings.Where(w => w.Contains("(associations.json)", StringComparison.Ordinal));

    private static string File(FileIconResolver r, string name, IconScope? scope = null, IconTheme theme = IconTheme.Dark) =>
        r.Resolve(name, isFolder: false, isExpanded: false, theme, scope);

    private static string Folder(FileIconResolver r, string name, bool expanded = false, IconScope? scope = null) =>
        r.Resolve(name, isFolder: true, expanded, IconTheme.Dark, scope);

    [Theory]
    [InlineData("readme.md", "markdown")]
    [InlineData("README.MD", "markdown")]
    [InlineData("index.ts", "typescript")]
    [InlineData("types.d.ts", "typescript-def")]
    [InlineData("App.D.TS", "typescript-def")]
    [InlineData("app.spec.ts", "test-ts")]
    [InlineData("a.b.c.spec.ts", "test-ts")]
    [InlineData("Dockerfile", "docker")]
    [InlineData(".gitignore", "git")]
    [InlineData(".GitIgnore", "git")]
    [InlineData(".eslintrc.json", "json")]
    [InlineData(".npmrc", "file")]
    [InlineData("Makefile", "file")]
    [InlineData("noext", "file")]
    [InlineData("trailing.", "file")]
    [InlineData("x.unknown", "file")]
    [InlineData("x.bad", "file")]
    [InlineData("page.html", "html")]
    [InlineData("script.ps1", "powershell")]
    public void Material_files_match_name_then_longest_extension_then_language(string name, string expected) =>
        Assert.Equal(expected, File(Create(dotDeck: null), name));

    [Fact]
    public void Material_entries_pointing_at_missing_icons_are_dropped_with_one_warning()
    {
        Create(dotDeck: null);
        Assert.Contains(Warnings, w => w.Contains("1개", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src", false, "folder-src")]
    [InlineData("src", true, "folder-src-open")]
    [InlineData("SRC", true, "folder-src-open")]
    [InlineData("agents", true, "folder-robot")] // no expanded entry and no "-open" icon: stays closed
    [InlineData("other", false, "folder")]
    [InlineData("other", true, "folder-open")]
    public void Folders_switch_between_closed_and_open_icons(string name, bool expanded, string expected) =>
        Assert.Equal(expected, Folder(Create(dotDeck: null), name, expanded));

    [Fact]
    public void Light_theme_prefers_light_variants()
    {
        var r = Create();
        Assert.Equal("json", File(r, "a.json"));
        Assert.Equal("json_light", File(r, "a.json", theme: IconTheme.Light));
        Assert.Equal("markdown", File(r, "a.md", theme: IconTheme.Light));
    }

    [Fact]
    public void Layers_apply_user_then_dotdeck_then_material_then_default()
    {
        const string user = """
            { "fileNames": { "CLAUDE.md": "toml" }, "fileExtensions": { "*.cfg": "settings", ".TMPL": "html", "d.ts": "json" },
              "folderNames": { "prompts": "folder-docs", "src": "folder-config" } }
            """;
        var r = Create(user);

        Assert.Equal("toml", File(r, "claude.md"));                // user beats DotDeck
        Assert.Equal("dotdeck-shared-doc", File(r, "AGENTS.md"));   // DotDeck beats Material (md)
        Assert.Equal("settings", File(r, "app.cfg"));               // user extension
        Assert.Equal("html", File(r, "page.tmpl"));
        Assert.Equal("json", File(r, "x.d.ts"));                   // user extension beats Material's longer match
        Assert.Equal("markdown", File(r, "notes.md"));              // Material
        Assert.Equal("file", File(r, "x.zzz"));                     // default
        Assert.Equal("folder-docs-open", Folder(r, "prompts", expanded: true)); // user folder opens with "-open"
        Assert.Equal("folder-config", Folder(r, "src"));
        Assert.Equal("dotdeck-folder-claude-open", Folder(r, ".claude", expanded: true));
        Assert.DoesNotContain(Warnings, w => !w.Contains("material-icons.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Common_names_get_tool_icons_only_inside_the_tool_folder()
    {
        var r = Create();
        var claudeAgents = new IconScope(IconTool.Claude, "agents");
        var agyConfig = new IconScope(IconTool.Antigravity, "config");

        Assert.Equal("dotdeck-folder-claude", Folder(r, "agents", scope: claudeAgents));
        Assert.Equal("folder-robot", Folder(r, "agents"));                                          // web project
        Assert.Equal("folder-robot", Folder(r, "agents", scope: new IconScope(IconTool.Claude, "x/agents"))); // not at the root
        Assert.Equal("folder-robot", Folder(r, "agents", scope: new IconScope(IconTool.Codex, "agents")));    // other tool
        Assert.Equal("dotdeck-folder-agy", Folder(r, "config", scope: agyConfig));
        Assert.Equal("folder-config-open", Folder(r, "config", expanded: true));
        Assert.Equal("dotdeck-claude-settings", File(r, "settings.json", new IconScope(IconTool.Claude, "settings.json")));
        Assert.Equal("settings", File(r, "settings.json"));
        Assert.Equal("dotdeck-agy-mcp", File(r, "mcp_config.json", new IconScope(IconTool.Antigravity, "config/mcp_config.json")));
        Assert.Equal("json", File(r, "mcp_config.json"));
    }

    [Fact]
    public void Cache_keeps_scopes_apart()
    {
        var r = Create();
        Assert.Equal("settings", File(r, "settings.json"));
        Assert.Equal("dotdeck-claude-settings", File(r, "settings.json", new IconScope(IconTool.Claude, "settings.json")));
        Assert.Equal("settings", File(r, "Settings.JSON"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    public void Unreadable_user_file_is_ignored_with_a_warning(string user)
    {
        var r = Create(user);

        Assert.Equal("dotdeck-claude-doc", File(r, "CLAUDE.md"));
        Assert.Contains(Warnings, w => w.Contains("associations.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_user_entries_are_dropped_one_by_one()
    {
        const string user = """
            { "fileExtensions": { "md": "no-such-icon", "txt": "custom:mine", "ts": 3, "cfg": "settings" },
              "folderNames": "nope", "colors": {} }
            """;
        var r = Create(user);

        Assert.Equal("markdown", File(r, "a.md"));
        Assert.Equal("settings", File(r, "a.cfg"));
        var warnings = Warnings.ToList();
        Assert.Contains(warnings, w => w.Contains("no-such-icon", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("custom:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'ts'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'folderNames'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'colors'", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_material_mapping_still_gives_default_icons()
    {
        var r = Create(material: "{ broken");
        Assert.Equal("file", File(r, "a.md"));
        Assert.Equal("folder-open", Folder(r, "src", expanded: true));
        Assert.Contains(Warnings, w => w.Contains("material-icons.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_dotdeck_rules_are_skipped()
    {
        var r = Create(dotDeck: """{ "scoped": [ { "tool": "vim", "match": "x", "icon": "file" }, { "tool": "claude", "match": "a", "icon": "nope" } ] }""");
        Assert.Equal(2, Warnings.Count(w => w.Contains("dotdeck-icons.json", StringComparison.Ordinal)));
        Assert.Equal("folder", Folder(r, "a", scope: new IconScope(IconTool.Claude, "a")));
    }

    [Fact]
    public void Dotdeck_rule_fields_and_sections_are_type_checked()
    {
        var r = Create(dotDeck: """
            { "fileNames": [], "extra": 1,
              "scoped": [ { "tool": "claude", "match": "agents", "folder": "true", "icon": "dotdeck-folder-claude" } ] }
            """);
        var warnings = Warnings.ToList();
        Assert.Contains(warnings, w => w.Contains("'fileNames'은 객체", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'extra'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("scoped[0]", StringComparison.Ordinal));
        // "folder": "true" is not a file rule either.
        Assert.Equal("file", File(r, "agents", new IconScope(IconTool.Claude, "agents")));
    }

    [Fact]
    public void Undecodable_strings_drop_only_their_entry()
    {
        // A lone surrogate parses as JSON but cannot be read as a .NET string.
        var r = Create(user: """{ "fileNames": { "a.txt": "\uD800", "b.txt": "markdown", "\uDC00": "html" } }""");
        Assert.Equal("markdown", File(r, "b.txt"));
        Assert.Equal("file", File(r, "a.txt"));
        Assert.Equal(2, UserWarnings.Count());
    }

    [Fact]
    public void Duplicate_sections_use_the_first_and_warn()
    {
        var r = Create(user: """{ "fileNames": { "a.txt": "markdown" }, "fileNames": { "a.txt": "html" } }""");
        Assert.Equal("markdown", File(r, "a.txt"));
        Assert.Contains(Warnings, w => w.Contains("두 번 이상", StringComparison.Ordinal));
    }

    [Fact]
    public void Warning_floods_are_summarized()
    {
        var entries = string.Join(", ", Enumerable.Range(0, 50).Select(i => $"\"f{i}\": \"nope\""));
        Create(user: $$"""{ "fileNames": { {{entries}} } }""");
        Assert.Equal(21, UserWarnings.Count());
        Assert.Contains(Warnings, w => w.Contains("그 밖의 경고 30건", StringComparison.Ordinal));
    }

    [Fact]
    public void The_user_layer_wins_over_longer_material_extensions_and_scoped_rules()
    {
        var r = Create(user: """{ "fileExtensions": { "ts": "html" }, "fileNames": { "settings.json": "toml" } }""");
        Assert.Equal("html", File(r, "types.d.ts"));
        Assert.Equal("toml", File(r, "settings.json", new IconScope(IconTool.Claude, "settings.json")));
    }

    [Fact]
    public void A_dotfile_named_like_an_extension_is_not_that_extension() =>
        Assert.Equal("file", File(Create(), ".ts"));

    [Fact]
    public void Light_defaults_apply_in_the_light_theme()
    {
        const string material = """
            {
              "iconDefinitions": { "file": {}, "file_light": {}, "folder": {}, "folder_light": {}, "folder-open": {}, "folder-open_light": {},
                                   "folder-src": {}, "folder-src_light": {}, "folder-src_light-open": {} },
              "file": "file", "folder": "folder", "folderExpanded": "folder-open",
              "light": { "file": "file_light", "folder": "folder_light", "folderExpanded": "folder-open_light",
                         "folderNames": { "src": "folder-src_light" } },
              "folderNames": { "src": "folder-src" }
            }
            """;
        var keys = new HashSet<string>(s_keys) { "file_light", "folder_light", "folder-open_light", "folder-src_light", "folder-src_light-open" };
        var r = FileIconResolver.Create(new IconSources(material, null, null, keys), _log);

        Assert.Equal("file_light", File(r, "x.unknown", theme: IconTheme.Light));
        Assert.Equal("folder_light", r.Resolve("x", true, false, IconTheme.Light));
        Assert.Equal("folder-open_light", r.Resolve("x", true, true, IconTheme.Light));
        // Only a closed light mapping: it still opens with its own "-open" variant, not the dark one.
        Assert.Equal("folder-src_light-open", r.Resolve("src", true, true, IconTheme.Light));
        Assert.Equal("file", File(r, "x.unknown"));
    }

    [Fact]
    public void Mappings_passed_to_the_constructor_are_copied()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["Readme"] = "markdown" };
        var keys = new HashSet<string>(s_keys);
        var r = new FileIconResolver(IconManifest.Empty with { Definitions = keys, Dark = new IconMappings { FileNames = names } },
            DotDeckIconRules.Empty, IconAssociations.Empty, keys);

        Assert.Equal("markdown", File(r, "README")); // case-insensitive even though the caller's map is not
        names["Other"] = "html";
        keys.Clear();
        Assert.Equal("file", File(r, "Other"));
        Assert.Contains("markdown", r.AvailableKeys);
    }
}

public sealed class IconScopeResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private IconScopeResolver Create() =>
        new(new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("roaming"), _temp.Combine("local")));

    [Fact]
    public void Items_under_the_builtin_homes_get_their_tool()
    {
        var r = Create();
        Assert.Equal(new IconScope(IconTool.Claude, "agents"), r.ScopeOf(_temp.Combine("home", ".claude", "agents")));
        Assert.Equal(new IconScope(IconTool.Codex, "config.toml"), r.ScopeOf(_temp.Combine("home", ".codex", "config.toml")));
        Assert.Equal(new IconScope(IconTool.Antigravity, "antigravity-cli/settings.json"),
            r.ScopeOf(_temp.Combine("home", ".gemini", "antigravity-cli", "settings.json")));
    }

    [Fact]
    public void Workspace_tool_folders_are_found_by_name_and_the_nearest_wins()
    {
        var r = Create();
        Assert.Equal(new IconScope(IconTool.Claude, "commands/a.md"), r.ScopeOf(@"D:\work\app\.Claude\commands\a.md"));
        Assert.Equal(new IconScope(IconTool.Shared, "skills"), r.ScopeOf("D:/work/app/.agents/skills/"));
        Assert.Equal(new IconScope(IconTool.Codex, "config.toml"), r.ScopeOf(_temp.Combine("home", ".claude", "projects", "p", ".codex", "config.toml")));
    }

    [Theory]
    [InlineData(@"D:\work\web\config")]
    [InlineData(@"D:\work\web\hooks\a.js")]
    [InlineData(@"D:\work\app\.claude")] // the root itself
    [InlineData(@"D:\work\app\.codex\.claude")] // a nested tool folder itself
    [InlineData(@"D:\work\.claude\..\settings.json")] // ".." leaves the tool folder
    [InlineData(@"D:.claude\agents")] // drive-relative: depends on a current directory
    [InlineData(@"agents\.claude\x")] // relative
    [InlineData(@"\\server\.claude\agents")] // the UNC share is not a tool folder
    [InlineData("")]
    [InlineData(null)]
    public void Other_paths_have_no_scope(string? path) => Assert.Null(Create().ScopeOf(path));
}

/// <summary>The real Material mapping shipped with the app.</summary>
public sealed class MaterialManifestTests
{
    private static readonly Lazy<FileIconResolver> s_resolver = new(() =>
    {
        var json = System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Icons", "material-icons.json"));
        var manifest = IconManifest.Parse(json, new List<string>());
        return new FileIconResolver(manifest, DotDeckIconRules.Empty, IconAssociations.Empty, manifest.Definitions);
    });

    [Theory]
    [InlineData("index.html", "html")]
    [InlineData("app.cfg", "settings")]
    [InlineData("setup.ini", "settings")]
    [InlineData("schema.sql", "database")]
    [InlineData("ci.yml", "yaml")]
    [InlineData("Dockerfile", "docker")]
    [InlineData(".gitignore", "git")]
    [InlineData(".editorconfig", "editorconfig")]
    [InlineData("package.json", "nodejs")]
    [InlineData("config.toml", "toml")]
    [InlineData("types.d.ts", "typescript-def")]
    [InlineData("a.spec.ts", "test-ts")]
    [InlineData("profile.ps1", "powershell")]
    [InlineData("Program.cs", "csharp")]
    [InlineData("Makefile", "makefile")]
    [InlineData(".ts", "file")]
    public void Common_files_resolve_to_their_material_icons(string name, string expected) =>
        Assert.Equal(expected, s_resolver.Value.Resolve(name, false, false, IconTheme.Dark));

    [Fact]
    public void Material_folders_open_and_close() =>
        Assert.Equal(("folder-src", "folder-src-open"),
            (s_resolver.Value.Resolve("src", true, false, IconTheme.Dark), s_resolver.Value.Resolve("src", true, true, IconTheme.Dark)));
}
