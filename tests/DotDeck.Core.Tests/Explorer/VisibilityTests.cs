using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

/// <summary>SPEC 2.3 visibility decision (section 7 tests of the explorer spec), without file system access.</summary>
public sealed class VisibilityEvaluatorTests
{
    private static readonly IReadOnlyList<string> s_ext = [".md", ".json"];

    private static VisibilityEvaluator Eval(params (string Key, VisibilityRule Rule)[] rules) =>
        Eval(false, rules);

    private static VisibilityEvaluator Eval(bool showAll, params (string Key, VisibilityRule Rule)[] rules) =>
        new(new VisibilitySettings
        {
            ShowAllFiles = showAll,
            Rules = VisibilitySettings.CreateRules(rules.Select(r => new KeyValuePair<string, VisibilityRule>(r.Key, r.Rule))),
        }, s_ext);

    [Fact]
    public void No_rules_shows_default_extensions_only()
    {
        var e = Eval();

        Assert.True(e.IsFileVisible("README.md"));
        Assert.True(e.IsFileVisible(@"src\a.json"));
        Assert.False(e.IsFileVisible(@"src\a.cs"));
    }

    [Fact]
    public void Folder_include_shows_only_default_extensions_below()
    {
        var e = Eval(("", VisibilityRule.Exclude), ("docs", VisibilityRule.Include));

        Assert.True(e.IsFileVisible(@"docs\a.md"));
        Assert.True(e.IsFileVisible(@"docs\deep\b.md"));
        Assert.False(e.IsFileVisible(@"docs\notes.log"));
        Assert.False(e.IsFileVisible("top.md"));
    }

    [Fact]
    public void Folder_include_with_file_exclude_hides_only_that_file()
    {
        var e = Eval(("docs", VisibilityRule.Include), (@"docs\b.md", VisibilityRule.Exclude));

        Assert.True(e.IsFileVisible(@"docs\a.md"));
        Assert.False(e.IsFileVisible(@"docs\b.md"));
        Assert.Equal(CheckState.Partial, e.CheckStateOf("docs", isDirectory: true));
    }

    [Fact]
    public void Folder_exclude_with_file_include_shows_only_that_file()
    {
        var e = Eval(("docs", VisibilityRule.Exclude), (@"docs\keep.md", VisibilityRule.Include));

        Assert.True(e.IsFileVisible(@"docs\keep.md"));
        Assert.False(e.IsFileVisible(@"docs\other.md"));
        Assert.Equal(CheckState.Partial, e.CheckStateOf("docs", isDirectory: true));
        Assert.False(e.IsSubtreeDead("docs"));
    }

    [Fact]
    public void Grandchild_include_under_an_excluded_child_of_an_included_folder_is_the_only_one_shown_there()
    {
        var e = Eval(("", VisibilityRule.Exclude), ("a", VisibilityRule.Include), (@"a\b", VisibilityRule.Exclude), (@"a\b\c", VisibilityRule.Include));

        Assert.True(e.IsFileVisible(@"a\b\c\x.md"));
        Assert.False(e.IsFileVisible(@"a\b\y.md"));
        Assert.True(e.IsFileVisible(@"a\z.md"));
        Assert.False(e.IsFileVisible("top.md"));
    }

    [Fact]
    public void Direct_include_beats_the_extension_rule()
    {
        var e = Eval(("docs", VisibilityRule.Include), (@"docs\notes.log", VisibilityRule.Include));

        Assert.True(e.IsFileVisible(@"docs\notes.log"));
        Assert.False(e.IsFileVisible(@"docs\other.log"));

        // The extension, not the user, hides other.log: the folder stays fully checked.
        Assert.Equal(CheckState.Checked, e.CheckStateOf("docs", isDirectory: true));
        Assert.Equal(CheckState.Unchecked, e.CheckStateOf(@"docs\other.log", isDirectory: false));
    }

    [Fact]
    public void Show_all_files_shows_everything_under_an_included_folder_and_reverts_when_off()
    {
        (string, VisibilityRule)[] rules = [("", VisibilityRule.Exclude), ("bin", VisibilityRule.Include)];

        Assert.True(Eval(true, rules).IsFileVisible(@"bin\app.dll"));
        Assert.False(Eval(true, rules).IsFileVisible("top.dll"));
        Assert.False(Eval(false, rules).IsFileVisible(@"bin\app.dll"));
        Assert.True(Eval(false, rules).IsFileVisible(@"bin\a.md"));
    }

    [Fact]
    public void Keys_ignore_case_and_slash_style()
    {
        var e = Eval(("Docs/Sub", VisibilityRule.Exclude));

        Assert.False(e.IsFileVisible(@"docs\sub\a.md"));
        Assert.Equal(VisibilityRule.Exclude, e.FolderState(@"DOCS\SUB\deeper"));
    }
}

public sealed class VisibilityEditorTests
{
    private static readonly IReadOnlyList<string> s_ext = [".md"];

    [Fact]
    public void Rule_equal_to_the_inherited_value_is_not_stored()
    {
        var editor = new VisibilityEditor(VisibilitySettings.Default, s_ext);

        editor.SetFolder("docs", check: true);
        editor.SetFile(@"src\a.md", check: true);
        Assert.Empty(editor.Current.Rules);

        editor.SetFolder("docs", check: false);
        editor.SetFile(@"docs\keep.md", check: true);
        Assert.Equal(2, editor.Current.Rules.Count);

        editor.SetFile(@"docs\keep.md", check: false);
        editor.SetFolder("docs", check: true);
        Assert.Empty(editor.Current.Rules);
    }

    [Fact]
    public void Checking_a_folder_removes_rules_below_and_counts_directly_checked_files()
    {
        var editor = new VisibilityEditor(VisibilitySettings.Default, s_ext);
        editor.SetFolder("docs", check: false);
        editor.SetFile(@"docs\a.md", check: true);
        editor.SetFile(@"docs\notes.log", check: true);
        editor.SetFolder(@"docs\sub", check: true);

        var impact = editor.PreviewFolder("docs", key => key.EndsWith("sub", StringComparison.Ordinal));

        Assert.Equal(1, impact.ClearedDirectFiles);
        Assert.Equal(3, impact.ClearedRules);
        editor.SetFolder("docs", check: true);
        Assert.Empty(editor.Current.Rules);
        Assert.False(editor.Evaluator.IsFileVisible(@"docs\notes.log"));
    }

    [Fact]
    public void Unchecking_the_root_then_checking_one_file_shows_only_that_file()
    {
        var editor = new VisibilityEditor(VisibilitySettings.Default, s_ext);

        editor.SetFolder(string.Empty, check: false);
        editor.SetFile(@"a\b\x.md", check: true);

        Assert.True(editor.Evaluator.IsFileVisible(@"a\b\x.md"));
        Assert.False(editor.Evaluator.IsFileVisible(@"a\b\y.md"));
        Assert.Equal(CheckState.Partial, editor.Evaluator.CheckStateOf(string.Empty, isDirectory: true));
    }
}

public sealed class ExtensionRulesTests
{
    [Theory]
    [InlineData(".LOG", ".log")]
    [InlineData("log", ".log")]
    [InlineData("Log", ".log")]
    [InlineData(" *.md ", ".md")]
    [InlineData(".d.ts", ".d.ts")]
    [InlineData("D.TS", ".d.ts")]
    [InlineData("", null)]
    [InlineData(".", null)]
    [InlineData("a/b", null)]
    [InlineData("a b", null)]
    [InlineData("a..b", null)]
    public void Input_is_normalized(string input, string? expected)
    {
        Assert.Equal(expected, ExtensionRules.Normalize(input));
    }

    [Fact]
    public void Matching_uses_ends_with_so_multi_part_extensions_work()
    {
        Assert.True(ExtensionRules.Matches("types.d.ts", [".d.ts"]));
        Assert.False(ExtensionRules.Matches("app.ts", [".d.ts"]));
        Assert.True(ExtensionRules.Matches("README.MD", [".md"]));
        Assert.True(ExtensionRules.Matches(".gitignore", [".gitignore"]));
        Assert.False(ExtensionRules.Matches("x.amd", [".md"]));
        Assert.Equal([".log", ".md"], ExtensionRules.NormalizeAll(["MD", ".log", "md", "?"]));
    }
}

public sealed class PathRewriteAndAutoIncludeTests
{
    private const string Root = @"D:\p";

    private static VisibilitySettings Rules(params (string Key, VisibilityRule Rule)[] rules) => new()
    {
        Rules = VisibilitySettings.CreateRules(rules.Select(r => new KeyValuePair<string, VisibilityRule>(r.Key, r.Rule))),
    };

    [Fact]
    public void Rules_pins_and_flat_order_follow_a_rename_with_their_descendants()
    {
        var visibility = Rules(("docs", VisibilityRule.Exclude), (@"docs\a.md", VisibilityRule.Include), ("docsx", VisibilityRule.Exclude));
        var view = new ProjectViewSettings { Pins = [@"docs\a.md", "README.md"], FlatOrder = [@"docs\a.md"] };

        var (v, w) = ExplorerPathRewriter.Apply(visibility, view, Root, [new PathChange.Moved(@"D:\p\docs", @"D:\p\guide")]);

        Assert.Equal(VisibilityRule.Exclude, v.Rules["guide"]);
        Assert.Equal(VisibilityRule.Include, v.Rules[@"guide\a.md"]);
        Assert.Equal(VisibilityRule.Exclude, v.Rules["docsx"]);
        Assert.False(v.Rules.ContainsKey("docs"));
        Assert.Equal([@"guide\a.md", "README.md"], w.Pins);
        Assert.Equal([@"guide\a.md"], w.FlatOrder);
    }

    [Fact]
    public void Moving_out_of_the_project_or_deleting_drops_the_rules()
    {
        var visibility = Rules((@"a\x.md", VisibilityRule.Include), ("b", VisibilityRule.Exclude));
        var view = new ProjectViewSettings { Pins = ["b"] };

        var (v, w) = ExplorerPathRewriter.Apply(visibility, view, Root,
            [new PathChange.Moved(@"D:\p\a\x.md", @"E:\elsewhere\x.md"), new PathChange.Removed(@"D:\p\b")]);

        Assert.Empty(v.Rules);
        Assert.Empty(w.Pins);
    }

    [Fact]
    public void Nothing_to_change_returns_the_same_instances()
    {
        var visibility = Rules(("a", VisibilityRule.Exclude));
        var view = ProjectViewSettings.Default;

        var (v, w) = ExplorerPathRewriter.Apply(visibility, view, Root, [new PathChange.Created(@"D:\p\new.md"), new PathChange.Removed(@"D:\other\a")]);

        Assert.Same(visibility, v);
        Assert.Same(view, w);
    }

    [Fact]
    public void A_hidden_new_file_gets_an_include_rule_and_a_visible_one_none()
    {
        IReadOnlyList<string> ext = [".md"];

        var hidden = VisibilityAutoInclude.EnsureVisible(VisibilitySettings.Default, ext, "notes.log", isDirectory: false);
        var visible = VisibilityAutoInclude.EnsureVisible(VisibilitySettings.Default, ext, "notes.md", isDirectory: false);

        Assert.Equal(VisibilityRule.Include, hidden.Rules["notes.log"]);
        Assert.Same(VisibilitySettings.Default, visible);
    }

    [Fact]
    public void A_new_empty_folder_gets_its_own_include_rule()
    {
        IReadOnlyList<string> ext = [".md"];

        var empty = VisibilityAutoInclude.EnsureVisible(VisibilitySettings.Default, ext, "new", isDirectory: true, hasVisibleContent: false);
        var withContent = VisibilityAutoInclude.EnsureVisible(VisibilitySettings.Default, ext, "full", isDirectory: true, hasVisibleContent: true);
        var excluded = VisibilityAutoInclude.EnsureVisible(Rules(("", VisibilityRule.Exclude)), ext, "full", isDirectory: true, hasVisibleContent: true);

        Assert.Equal(VisibilityRule.Include, empty.Rules["new"]);
        Assert.Same(VisibilitySettings.Default, withContent);
        Assert.Equal(VisibilityRule.Include, excluded.Rules["full"]);
    }
}

/// <summary>Normal-mode layouts over real temporary folders.</summary>
public sealed class VisibleProjectSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private void Touch(string relative)
    {
        var path = _temp.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private ProjectDefinition Project => new() { Id = "p", Kind = ProjectKind.Folder, Name = "P", Root = _temp.Path };

    private VisibleProjectSource Source(ExplorerViewMode mode, VisibilitySettings? visibility = null, ProjectViewSettings? view = null, IFileStore? store = null)
    {
        store ??= PhysicalFileStore.Instance;
        var all = new FolderProjectSource(Project, _temp.Path, store);
        return new VisibleProjectSource(all, store, new VisibilityEvaluator(visibility ?? VisibilitySettings.Default, [".md"]),
            (view ?? ProjectViewSettings.Default) with { Mode = mode });
    }

    private static List<string> Rows(IProjectSource source, params string[] expanded) =>
        ExplorerTreeBuilder.Build(source, new HashSet<string>(expanded, ExplorerTreeBuilder.KeyComparer)).Rows
            .Select(r => new string(' ', r.Depth * 2) + r.Node.Name).ToList();

    [Fact]
    public void Only_folders_that_lead_to_visible_files_are_shown()
    {
        Touch("README.md");
        Touch("src/app.cs");
        Touch("docs/guide/a.md");

        Assert.Equal(["docs", "  guide", "    a.md", "README.md"], Rows(Source(ExplorerViewMode.Tree), "docs", @"docs\guide"));
    }

    [Fact]
    public void Excluded_folder_with_one_checked_file_shows_that_file_and_its_path()
    {
        Touch("docs/a/keep.md");
        Touch("docs/a/other.md");
        Touch("docs/b/x.md");
        var visibility = new VisibilitySettings
        {
            Rules = VisibilitySettings.CreateRules([new("docs", VisibilityRule.Exclude), new(@"docs\a\keep.md", VisibilityRule.Include)]),
        };

        Assert.Equal(["docs", "  a", "    keep.md"], Rows(Source(ExplorerViewMode.Tree, visibility), "docs", @"docs\a"));
    }

    [Fact]
    public void A_file_created_outside_the_app_follows_the_default_extensions_on_the_next_build()
    {
        Touch("a.md");
        Assert.Equal(["a.md"], Rows(Source(ExplorerViewMode.Tree)));

        Touch("b.md");
        Touch("c.log");

        Assert.Equal(["a.md", "b.md"], Rows(Source(ExplorerViewMode.Tree)));
    }

    [Fact]
    public void Compact_tree_merges_single_child_chains_and_breaks_at_branches_and_include_folders()
    {
        Touch("src/DotDeck.App/Themes/Dark.md");
        Touch("src/DotDeck.App/Themes/Light.md");
        Touch("one/two/three/four/x.md");
        Touch("branch/left/a.md");
        Touch("branch/right/b.md");
        var visibility = new VisibilitySettings { Rules = VisibilitySettings.CreateRules([new(@"one\two", VisibilityRule.Include)]) };
        var source = Source(ExplorerViewMode.Compact, visibility);

        var root = source.GetChildren(null);

        Assert.Equal(["branch", "one/two", "src/DotDeck.App/Themes"], root.Select(n => n.Name));
        Assert.Equal(["src", "DotDeck.App", "Themes"], root[2].Chain!.Select(c => c.Name));
        Assert.Equal(@"src\DotDeck.App\Themes", root[2].RelativePath);
        Assert.Null(root[0].Chain);
        Assert.Equal(["three/four"], source.GetChildren(root[1]).Select(n => n.Name));
    }

    [Fact]
    public void Flat_view_lists_visible_files_with_their_folder_so_same_names_differ()
    {
        Touch("a/README.md");
        Touch("b/README.md");
        Touch("README.md");
        Touch("b/skip.log");

        var files = Source(ExplorerViewMode.Flat).GetChildren(null);

        Assert.Equal(["README.md", "README.md", "README.md"], files.Select(f => f.Name));
        Assert.Equal(["", "a", "b"], files.Select(f => f.FlatDirectory));
    }

    [Fact]
    public void Flat_custom_order_puts_ordered_files_first_then_the_rest_by_name()
    {
        Touch("z.md");
        Touch("a.md");
        Touch("m.md");
        var view = new ProjectViewSettings { FlatSort = FlatSort.Custom, FlatOrder = ["m.md", "gone.md", "z.md"] };

        Assert.Equal(["m.md", "z.md", "a.md"], Source(ExplorerViewMode.Flat, view: view).GetChildren(null).Select(f => f.Name));
    }

    [Theory]
    [InlineData(ExplorerViewMode.Tree)]
    [InlineData(ExplorerViewMode.Compact)]
    [InlineData(ExplorerViewMode.Flat)]
    public void Pinned_items_come_first_and_are_not_repeated(ExplorerViewMode mode)
    {
        Touch("docs/a.md");
        Touch("docs/b.md");
        Touch("README.md");
        Touch("notes.log");
        var view = new ProjectViewSettings { Pins = ["notes.log", "docs"] };

        var root = Source(mode, view: view).GetChildren(null);

        Assert.Equal(["notes.log", "docs"], root.Take(2).Select(n => n.Name));
        Assert.All(root.Take(2), n => Assert.True(n.IsPinned));
        Assert.Equal(["README.md"], root.Skip(2).Select(n => n.Name));
    }

    [Theory]
    [InlineData(ExplorerViewMode.Tree)]
    [InlineData(ExplorerViewMode.Compact)]
    public void A_pinned_file_inside_a_folder_is_not_repeated_in_that_folder(ExplorerViewMode mode)
    {
        Touch("docs/a.md");
        Touch("docs/b.md");
        var source = Source(mode, view: new ProjectViewSettings { Pins = [@"docs\a.md"] });

        var root = source.GetChildren(null);

        Assert.Equal(["a.md", "docs"], root.Select(n => n.Name));
        Assert.Equal(["b.md"], source.GetChildren(root[1]).Select(n => n.Name));
    }

    [Fact]
    public void A_pinned_item_inside_an_excluded_folder_is_not_shown()
    {
        Touch("bin/out.md");
        Touch("README.md");
        var source = Source(ExplorerViewMode.Tree, view: new ProjectViewSettings { Pins = [@"bin\out.md", "bin"] });

        Assert.Equal(["README.md"], source.GetChildren(null).Select(n => n.Name));
    }

    [Fact]
    public void A_spent_entry_budget_stops_listing_folders()
    {
        for (var i = 0; i < 20; i++)
        {
            Touch($"f{i:00}/x.log");
        }

        var store = new CountingFileStore();
        var all = new FolderProjectSource(Project, _temp.Path, store);
        var source = new VisibleProjectSource(all, store, new VisibilityEvaluator(VisibilitySettings.Default, [".md"]),
            ProjectViewSettings.Default with { Mode = ExplorerViewMode.Tree }, entryBudget: 25);

        var rows = Rows(source);

        Assert.True(source.IsTruncated);

        // Root (20 entries) and five folders (1 entry each) fit the budget: those five were seen to hold nothing
        // visible. Once it is spent, the other 15 are assumed to have content without being listed.
        Assert.Equal(6, store.Listed.Count);
        Assert.Equal(15, rows.Count);
    }

    [Fact]
    public void Folders_that_cannot_contain_anything_visible_are_not_listed()
    {
        Touch("big/1.md");
        Touch("big/deep/2.md");
        Touch("keep.md");
        var store = new CountingFileStore();
        var visibility = new VisibilitySettings { Rules = VisibilitySettings.CreateRules([new("big", VisibilityRule.Exclude)]) };

        var rows = Rows(Source(ExplorerViewMode.Tree, visibility, store: store));

        Assert.Equal(["keep.md"], rows);
        Assert.DoesNotContain(store.Listed, d => d.Contains("big", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Included_empty_folder_is_shown()
    {
        Directory.CreateDirectory(_temp.Combine("empty"));
        var visibility = new VisibilitySettings { Rules = VisibilitySettings.CreateRules([new("empty", VisibilityRule.Include)]) };

        Assert.Equal(["empty"], Rows(Source(ExplorerViewMode.Tree, visibility)));
        Assert.Empty(Rows(Source(ExplorerViewMode.Tree)));
    }

    private sealed class CountingFileStore : IFileStore
    {
        private readonly PhysicalFileStore _inner = PhysicalFileStore.Instance;

        public List<string> Listed { get; } = [];

        public IReadOnlyList<FileSystemEntry> GetChildren(string directory)
        {
            Listed.Add(directory);
            return _inner.GetChildren(directory);
        }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public byte[] ReadHead(string path, int maxBytes) => _inner.ReadHead(path, maxBytes);

        public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);

        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateFile(string path) => _inner.CreateFile(path);

        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);

        public void Copy(string source, string destination) => _inner.Copy(source, destination);

        public void ReplaceFile(string source, string destination) => _inner.ReplaceFile(source, destination);

        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }
}

/// <summary>projects.json v2 storage and the v1 migration.</summary>
public sealed class ProjectsFileV2Tests
{
    [Fact]
    public void Visibility_view_and_pins_round_trip_byte_identically()
    {
        var project = new ProjectDefinition
        {
            Id = "p",
            Kind = ProjectKind.Folder,
            Name = "P",
            Root = @"D:\p",
            Visibility = new VisibilitySettings
            {
                DefaultExtensions = [".cs", ".md"],
                Rules = VisibilitySettings.CreateRules([new("", VisibilityRule.Exclude), new(@"src\App", VisibilityRule.Include), new(@"docs\a.log", VisibilityRule.Include)]),
            },
            View = new ProjectViewSettings { Mode = ExplorerViewMode.Flat, FlatSort = FlatSort.Custom, FlatOrder = [@"docs\a.log"], Pins = ["docs", "README.md"] },
        };

        var text = ProjectsFileSerializer.Serialize(new ProjectsFileContent([project], [], null));
        var parsed = ProjectsFileSerializer.Parse(text);
        var again = ProjectsFileSerializer.Serialize(parsed);

        Assert.Equal(text, again);
        Assert.Equal(project.Visibility, parsed.Projects[0].Visibility);
        Assert.Equal(project.View, parsed.Projects[0].View);
        Assert.Contains("\"src/App\": \"include\"", text, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_inside_visibility_and_view_are_kept()
    {
        const string json = """
            { "schemaVersion": 2, "projects": [ { "id": "p", "kind": "folder", "name": "P", "root": "D:\\p",
              "visibility": { "showAllFiles": false, "rules": {}, "future": 1 }, "view": { "mode": "tree", "zoom": [2] } } ] }
            """;

        var text = ProjectsFileSerializer.Serialize(ProjectsFileSerializer.Parse(json));

        Assert.Contains("\"future\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"zoom\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void V1_filter_and_visible_items_migrate_to_equivalent_rules()
    {
        const string json = """
            { "schemaVersion": 1, "projects": [
              { "id": "a", "kind": "folder", "name": "A", "root": "D:\\a", "filter": "configDocs", "visibleItems": ["agents", "agents\\x.md", "CLAUDE.md"] },
              { "id": "b", "kind": "folder", "name": "B", "root": "D:\\b", "filter": "all" },
              { "id": "builtin.claude", "kind": "builtin", "builtinKey": "claude" } ] }
            """;

        var projects = ProjectsFileSerializer.Parse(json).Projects;
        var a = projects.Single(p => p.Id == "a").Visibility;
        var b = projects.Single(p => p.Id == "b").Visibility;
        var claude = projects.Single(p => p.Id == "builtin.claude").Visibility;

        Assert.False(a.ShowAllFiles);
        Assert.Equal(ProjectsFileSerializer.LegacyConfigDocsExtensions, a.DefaultExtensions);
        Assert.Equal(3, a.Rules.Count);
        Assert.Equal(VisibilityRule.Exclude, a.Rules[""]);
        Assert.Equal(VisibilityRule.Include, a.Rules["agents"]);
        Assert.Equal(VisibilityRule.Include, a.Rules["CLAUDE.md"]);
        var e = new VisibilityEvaluator(a, a.DefaultExtensions!);
        Assert.True(e.IsFileVisible(@"agents\deep\y.md"));
        Assert.False(e.IsFileVisible("settings.json"));
        Assert.True(b.ShowAllFiles);
        Assert.Empty(b.Rules);
        Assert.True(claude.ShowAllFiles);

        var written = ProjectsFileSerializer.Serialize(new ProjectsFileContent(projects, [], null));
        Assert.DoesNotContain("\"filter\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleItems", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_keeps_a_copy_of_a_v1_file_before_saving_v2()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("projects.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "projects": [] }""");
        var registry = new ProjectRegistry(PhysicalFileStore.Instance, path, TimeProvider.System, new ListAppLog());

        registry.Load();
        registry.SetHidden("builtin.codex", true);

        var backup = Assert.Single(Directory.GetFiles(temp.Path, "projects.json.v1-*.bak"));
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(backup), StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unusable_default_extension_keeps_the_project_entry_verbatim()
    {
        const string json = """
            { "schemaVersion": 2, "projects": [ { "id": "p", "kind": "folder", "name": "P", "root": "D:\\p",
              "visibility": { "defaultExtensions": ["*.md", "a/b"] } } ] }
            """;

        var content = ProjectsFileSerializer.Parse(json);
        var text = ProjectsFileSerializer.Serialize(content);

        Assert.DoesNotContain(content.Projects, p => p.Id == "p");
        Assert.Contains("a/b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v1_file_that_cannot_be_backed_up_opens_read_only_and_is_not_replaced()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("projects.json");
        const string v1 = """{ "schemaVersion": 1, "projects": [] }""";
        File.WriteAllText(path, v1);
        var registry = new ProjectRegistry(new NoBackupFileStore(), path, TimeProvider.System, new ListAppLog());

        var load = registry.Load();

        Assert.True(registry.IsReadOnly);
        Assert.NotNull(load.Message);
        Assert.ThrowsAny<Exception>(() => registry.SetHidden("builtin.codex", true));
        Assert.Equal(v1, File.ReadAllText(path));
    }

    /// <summary>Fails to write any *.bak file (a full or read-only disk for the backup).</summary>
    private sealed class NoBackupFileStore : IFileStore
    {
        private readonly PhysicalFileStore _inner = PhysicalFileStore.Instance;

        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => _inner.GetChildren(directory);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public FileSystemEntry? GetEntry(string path) => _inner.GetEntry(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public byte[] ReadHead(string path, int maxBytes) => _inner.ReadHead(path, maxBytes);

        public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);

        public void WriteAllBytes(string path, byte[] bytes)
        {
            if (path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("disk full");
            }

            _inner.WriteAllBytes(path, bytes);
        }

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void CreateFile(string path) => _inner.CreateFile(path);

        public void Move(string source, string destination, bool overwrite = false) => _inner.Move(source, destination, overwrite);

        public void Copy(string source, string destination) => _inner.Copy(source, destination);

        public void ReplaceFile(string source, string destination) => _inner.ReplaceFile(source, destination);

        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => _inner.AcquireLock(lockPath, timeout);
    }

    [Fact]
    public void Pins_and_rules_survive_a_rename_through_the_registry()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("projects.json");
        var registry = new ProjectRegistry(PhysicalFileStore.Instance, path, TimeProvider.System, new ListAppLog());
        registry.Load();
        var project = registry.AddFolder("P", @"D:\p");
        registry.SetView(project.Id, new ProjectViewSettings { Pins = [@"docs\a.md"] });
        registry.SetVisibility(project.Id, new VisibilitySettings { Rules = VisibilitySettings.CreateRules([new("docs", VisibilityRule.Exclude)]) });

        registry.UpdateAll(p => p.Root is { } root
            ? ExplorerPathRewriter.Apply(p.Visibility, p.View, root, [new PathChange.Moved(@"D:\p\docs", @"D:\p\guide")]) is var (v, w)
                && (!ReferenceEquals(v, p.Visibility) || !ReferenceEquals(w, p.View)) ? p with { Visibility = v, View = w } : p
            : p);

        var again = new ProjectRegistry(PhysicalFileStore.Instance, path, TimeProvider.System, new ListAppLog());
        again.Load();
        var loaded = again.Find(project.Id)!;
        Assert.Equal([@"guide\a.md"], loaded.View.Pins);
        Assert.Equal(VisibilityRule.Exclude, loaded.Visibility.Rules["guide"]);
    }
}
