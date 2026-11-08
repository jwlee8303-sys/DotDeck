using System.Text.Json.Nodes;
using DotDeck.Core.Explorer;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Explorer;

public sealed class CustomFilesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly ListAppLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.Combine("roaming", "projects.json");

    private ProjectRegistry LoadNew()
    {
        var registry = new ProjectRegistry(PhysicalFileStore.Instance, FilePath, _time, _log);
        registry.Load();
        return registry;
    }

    private string Touch(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData("")]
    public void Validate_rejects_relative_paths_and_secret_files(string path)
    {
        Assert.NotNull(CustomFileRules.Validate(path));
    }

    [Fact]
    public void Validate_accepts_full_paths_of_ordinary_files()
    {
        Assert.Null(CustomFileRules.Validate(@"C:\Users\me\.wslconfig"));
        Assert.Null(CustomFileRules.Validate(@"C:\work\.env.example"));
    }

    [Fact]
    public void Registry_adds_and_removes_custom_files_and_they_round_trip()
    {
        var registry = LoadNew();
        var project = registry.AddFolder("Web", _temp.Combine("web"), null);
        var file = _temp.Combine("outside", "notes.md");

        registry.AddCustomFile(project.Id, file);
        registry.AddCustomFile(project.Id, file.ToUpperInvariant());
        registry.AddCustomFile("builtin.claude", file);

        var reloaded = LoadNew();
        Assert.Equal([file], reloaded.Find(project.Id)!.CustomFiles);
        Assert.Equal([file], reloaded.Find("builtin.claude")!.CustomFiles);

        reloaded.RemoveCustomFile(project.Id, file.ToUpperInvariant());

        Assert.Empty(LoadNew().Find(project.Id)!.CustomFiles);
    }

    [Fact]
    public void Registry_refuses_secret_files_and_the_system_project()
    {
        var registry = LoadNew();

        registry.AddCustomFile("builtin.claude", _temp.Combine(".env"));
        Assert.Throws<InvalidOperationException>(() => registry.AddCustomFile("builtin.system", _temp.Combine("a.txt")));
        Assert.Single(registry.Find("builtin.claude")!.CustomFiles);
    }

    [Fact]
    public void Empty_list_and_shown_node_are_not_written()
    {
        var registry = LoadNew();
        var project = registry.AddFolder("Web", _temp.Combine("web"), null);

        var entry = ReadEntry(project.Id);
        Assert.Null(entry["customFiles"]);
        Assert.Null(entry["visibility"]?["hideCustomFiles"]);

        registry.SetVisibility(project.Id, project.Visibility with { HideCustomFiles = true });

        Assert.True((bool)ReadEntry(project.Id)["visibility"]!["hideCustomFiles"]!);
        Assert.True(LoadNew().Find(project.Id)!.Visibility.HideCustomFiles);
    }

    [Fact]
    public void Entry_with_invalid_custom_file_is_preserved_verbatim()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 2, "projects": [
              { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", "customFiles": [ "relative.txt" ], "future": 1 } ] }
            """);
        var registry = LoadNew();
        Assert.Null(registry.Find("p1"));

        registry.SetHidden("builtin.codex", true);

        var raw = ReadEntry("p1");
        Assert.Equal("relative.txt", (string?)raw["customFiles"]![0]);
        Assert.Equal(1, (int)raw["future"]!);
    }

    [Fact]
    public void Unknown_keys_survive_a_custom_file_change()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 2, "projects": [
              { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", "future": { "x": 1 },
                "visibility": { "futureRule": true } } ] }
            """);
        var registry = LoadNew();

        registry.AddCustomFile("p1", @"C:\notes\a.md");

        var raw = ReadEntry("p1");
        Assert.Equal(1, (int)raw["future"]!["x"]!);
        Assert.True((bool)raw["visibility"]!["futureRule"]!);
        Assert.Equal(@"C:\notes\a.md", (string?)raw["customFiles"]![0]);
    }

    [Fact]
    public void Source_puts_the_node_first_and_lists_files_with_missing_ones_marked()
    {
        Touch("root", "a.md");
        var existing = Touch("outside", "notes.md");
        var missing = _temp.Combine("outside", "gone.md");
        var root = _temp.Combine("root");
        var project = new ProjectDefinition { Id = "p1", Kind = ProjectKind.Folder, Name = "P", Root = root };
        var source = new CustomFilesProjectSource(new FolderProjectSource(project, root, PhysicalFileStore.Instance), PhysicalFileStore.Instance, [existing, missing]);

        var top = source.GetChildren(null);
        Assert.Equal(CustomFilesProjectSource.NodeId, top[0].Id);
        Assert.Equal(CustomFileRules.NodeName, top[0].Name);
        Assert.Contains(top, n => n.Name == "a.md");

        var files = source.GetChildren(top[0]);
        Assert.Equal(["notes.md", "gone.md (없음)"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.True(f.IsCustomFile));
        Assert.All(files, f => Assert.Null(f.RelativePath));
        Assert.Equal(existing, files[0].Id);
        Assert.Same(source.Inner, CustomFilesProjectSource.Unwrap(source));
    }

    [Fact]
    public void Editor_toggles_the_node_without_touching_rules()
    {
        var initial = VisibilitySettings.Default with { Rules = new Dictionary<string, VisibilityRule> { ["docs"] = VisibilityRule.Exclude } };
        var editor = new VisibilityEditor(initial, [".md"]);

        editor.SetCustomFilesVisible(false);

        Assert.True(editor.Current.HideCustomFiles);
        Assert.Equal(VisibilityRule.Exclude, editor.Current.Rules["docs"]);
        Assert.False(editor.Current.ContentEquals(initial));

        editor.SetCustomFilesVisible(true);

        Assert.True(editor.Current.ContentEquals(initial));
    }

    [Fact]
    public void Rewrite_follows_renamed_files_and_moved_folders()
    {
        IReadOnlyList<string> files = [@"C:\a\x.md", @"C:\a\sub\y.md", @"C:\b\z.md"];

        var result = CustomFileRules.Rewrite(files,
        [
            new PathChange.Moved(@"C:\a\x.md", @"C:\a\renamed.md"),
            new PathChange.Moved(@"C:\A\sub", @"C:\c\sub2"),
            new PathChange.Removed(@"C:\b\z.md"),
        ]);

        Assert.Equal([@"C:\a\renamed.md", @"C:\c\sub2\y.md", @"C:\b\z.md"], result);
    }

    [Fact]
    public void Rewrite_returns_the_same_list_when_nothing_moves()
    {
        IReadOnlyList<string> files = [@"C:\a\x.md"];

        Assert.Same(files, CustomFileRules.Rewrite(files, [new PathChange.Moved(@"C:\a\xy.md", @"C:\a\q.md"), new PathChange.Created(@"C:\a\n.md")]));
    }

    [Fact]
    public void Rewrite_drops_a_file_renamed_into_a_secret_file()
    {
        IReadOnlyList<string> files = [@"C:\a\notes.txt", @"C:\a\keep.md"];

        var result = CustomFileRules.Rewrite(files, [new PathChange.Moved(@"C:\a\notes.txt", @"C:\a\.env")]);

        Assert.Equal([@"C:\a\.env", @"C:\a\keep.md"], result);
    }

    [Fact]
    public void Rewritten_list_still_loads_after_rename_to_secret_file()
    {
        var registry = LoadNew();
        var project = registry.AddFolder("Web", _temp.Combine("web"), null);
        registry.AddCustomFile(project.Id, _temp.Combine("notes.txt"));

        registry.UpdateAll(p => p.Id == project.Id
            ? p with { CustomFiles = CustomFileRules.Rewrite(p.CustomFiles, [new PathChange.Moved(_temp.Combine("notes.txt"), _temp.Combine(".env"))]) }
            : p);

        var reloaded = LoadNew().Find(project.Id);
        Assert.NotNull(reloaded);
        Assert.Equal([_temp.Combine(".env")], reloaded.CustomFiles);
    }

    [Fact]
    public void Node_only_source_lists_the_node_when_the_root_is_missing()
    {
        var file = Touch("outside", "notes.md");
        var root = _temp.Combine("missing-root");
        var project = new ProjectDefinition { Id = "p1", Kind = ProjectKind.Folder, Name = "P", Root = root };
        var source = new CustomFilesProjectSource(new FolderProjectSource(project, root, PhysicalFileStore.Instance), PhysicalFileStore.Instance, [file]);

        Assert.Equal(ProjectRootState.Missing, ExplorerTreeBuilder.Build(source, new HashSet<string>(), TestContext.Current.CancellationToken).RootState);

        var rows = ExplorerTreeBuilder.Build(source.NodeOnly(), new HashSet<string>(ExplorerTreeBuilder.KeyComparer) { CustomFilesProjectSource.NodeId }, TestContext.Current.CancellationToken).Rows;
        Assert.Equal([CustomFilesProjectSource.NodeId, file], rows.Select(r => r.Node.Id));
    }

    private JsonObject ReadEntry(string id) =>
        JsonNode.Parse(File.ReadAllText(FilePath))!["projects"]!.AsArray().Single(p => (string?)p!["id"] == id)!.AsObject();
}
