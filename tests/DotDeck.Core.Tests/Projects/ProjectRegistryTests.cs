using System.Text.Json.Nodes;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Projects;

public sealed class ProjectRegistryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly ListAppLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.Combine("roaming", "projects.json");

    private ProjectRegistry CreateRegistry() => new(PhysicalFileStore.Instance, FilePath, _time, _log);

    private ProjectRegistry LoadNew()
    {
        var registry = CreateRegistry();
        registry.Load();
        return registry;
    }

    [Fact]
    public void Missing_file_creates_and_saves_builtin_defaults()
    {
        var registry = CreateRegistry();

        var result = registry.Load();

        Assert.Equal(ProjectRegistryLoadStatus.CreatedDefaults, result.Status);
        Assert.Equal(["builtin.claude", "builtin.codex", "builtin.system"], registry.Projects.Select(p => p.Id));
        Assert.True(File.Exists(FilePath));
        // New built-ins start from the global default extensions without rules.
        Assert.All(registry.Projects, p => Assert.Equal(VisibilitySettings.Default, p.Visibility));
    }

    [Fact]
    public void Added_project_round_trips_through_file()
    {
        var registry = LoadNew();

        var added = registry.AddFolder("DotDeck", @"D:\project\DotDeck\", "#68217a");

        var reloaded = LoadNew();
        var project = reloaded.Find(added.Id);
        Assert.NotNull(project);
        Assert.Equal("DotDeck", project.Name);
        Assert.Equal(@"D:\project\DotDeck", project.Root);
        Assert.Equal("#68217A", project.Color);
        Assert.Null(project.Visibility.DefaultExtensions);
        Assert.False(project.Visibility.ShowAllFiles);
        Assert.Equal(3, project.Order);
    }

    [Fact]
    public void Unknown_fields_and_unrecognized_entries_are_preserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            {
              "schemaVersion": 1,
              "futureRoot": { "x": 1 },
              "projects": [
                { "id": "builtin.claude", "kind": "builtin", "builtinKey": "claude", "order": 0, "pinned": true },
                { "id": "p1", "kind": "folder", "name": "Web", "root": "D:\\web", "order": 1, "icon": "globe" },
                { "id": "p2", "kind": "remote", "url": "https://example" }
              ]
            }
            """);
        var registry = LoadNew();

        registry.Rename("p1", "웹게임");

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.Equal(1, (int)json["futureRoot"]!["x"]!);
        var projects = json["projects"]!.AsArray();
        Assert.Contains(projects, p => (string?)p!["id"] == "builtin.claude" && (bool?)p["pinned"] == true);
        Assert.Contains(projects, p => (string?)p!["id"] == "p1" && (string?)p["icon"] == "globe" && (string?)p["name"] == "웹게임");
        Assert.Contains(projects, p => (string?)p!["kind"] == "remote" && (string?)p["url"] == "https://example");
    }

    [Fact]
    public void Missing_builtins_are_restored_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "schemaVersion": 1, "projects": [ { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", "order": 0 } ] }""");

        var registry = LoadNew();

        Assert.Equal(4, registry.Projects.Count);
        Assert.Contains(registry.Projects, p => p.Builtin == BuiltinProject.System);
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_defaults_are_created()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var registry = CreateRegistry();

        var result = registry.Load();

        Assert.Equal(ProjectRegistryLoadStatus.RecoveredFromCorrupt, result.Status);
        var corrupt = Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "projects.json.corrupt-*").Single();
        Assert.Equal("{ not json", File.ReadAllText(corrupt));
        Assert.Equal(3, registry.Projects.Count);
        Assert.False(registry.IsReadOnly);
    }

    [Fact]
    public void Newer_schema_is_read_only_and_never_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        const string newer = """{ "schemaVersion": 99, "projects": [] }""";
        File.WriteAllText(FilePath, newer);
        var registry = CreateRegistry();

        var result = registry.Load();

        Assert.Equal(ProjectRegistryLoadStatus.ReadOnlyNewerSchema, result.Status);
        Assert.True(registry.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => registry.AddFolder("A", @"D:\a"));
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Locked_file_is_left_untouched_and_registry_is_read_only()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        const string original = """{ "schemaVersion": 1, "projects": [] }""";
        File.WriteAllText(FilePath, original);
        var registry = CreateRegistry();

        ProjectRegistryLoadResult result;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = registry.Load();
        }

        Assert.Equal(ProjectRegistryLoadStatus.ReadOnlyUnreadable, result.Status);
        Assert.True(registry.IsReadOnly);
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Builtin_projects_cannot_be_removed_renamed_or_rerooted()
    {
        var registry = LoadNew();

        Assert.Throws<InvalidOperationException>(() => registry.Remove("builtin.claude"));
        Assert.Throws<InvalidOperationException>(() => registry.Rename("builtin.codex", "x"));
        Assert.Throws<InvalidOperationException>(() => registry.ChangeRoot("builtin.codex", @"D:\x"));
    }

    [Fact]
    public void Builtin_projects_can_be_hidden_and_reordered()
    {
        var registry = LoadNew();

        registry.SetHidden("builtin.system", true);
        registry.MoveTo("builtin.system", 0);

        var reloaded = LoadNew();
        Assert.Equal("builtin.system", reloaded.Projects[0].Id);
        Assert.True(reloaded.Projects[0].Hidden);
        Assert.Equal([0, 1, 2], reloaded.Projects.Select(p => p.Order));
    }

    [Fact]
    public void Remove_only_updates_the_list_not_the_disk()
    {
        var folder = _temp.Combine("userproject");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.md"), "x");
        var registry = LoadNew();
        var added = registry.AddFolder("User", folder);

        registry.Remove(added.Id);

        Assert.Null(LoadNew().Find(added.Id));
        Assert.True(File.Exists(Path.Combine(folder, "keep.md")));
    }

    [Theory]
    [InlineData("", @"D:\a")]
    [InlineData("   ", @"D:\a")]
    [InlineData("A", "relative\\path")]
    [InlineData("A", "")]
    public void Invalid_name_or_root_is_rejected(string name, string root)
    {
        var registry = LoadNew();

        Assert.Throws<ArgumentException>(() => registry.AddFolder(name, root));
        Assert.Equal(3, registry.Projects.Count);
    }

    [Fact]
    public void Duplicate_name_or_root_is_rejected_case_insensitively()
    {
        var registry = LoadNew();
        registry.AddFolder("Web", @"D:\web");

        Assert.Throws<ArgumentException>(() => registry.AddFolder("WEB", @"D:\other"));
        Assert.Throws<ArgumentException>(() => registry.AddFolder("Other", @"d:\WEB\"));
        Assert.Throws<ArgumentException>(() => registry.AddFolder("Claude 전역", @"D:\c"));
    }

    [Fact]
    public void Invalid_color_is_rejected()
    {
        var registry = LoadNew();

        Assert.Throws<ArgumentException>(() => registry.AddFolder("A", @"D:\a", "red"));
    }

    [Fact]
    public void Drive_root_is_kept_with_separator()
    {
        var registry = LoadNew();

        var added = registry.AddFolder("Drive", @"E:\");

        Assert.Equal(@"E:\", added.Root);
    }

    [Fact]
    public void Failed_save_leaves_state_unchanged()
    {
        var registry = LoadNew();
        var before = registry.Projects;
        // Make the target path a directory so the atomic replace fails.
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);

        Assert.ThrowsAny<Exception>(() => registry.AddFolder("A", @"D:\a"));

        Assert.Same(before, registry.Projects);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }

    [Fact]
    public void Changed_is_raised_after_successful_commit()
    {
        var registry = LoadNew();
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        registry.AddFolder("A", @"D:\a");
        registry.SetVisibility("builtin.claude", new VisibilitySettings { ShowAllFiles = true });

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Excluded_folders_are_trimmed_and_deduplicated_and_null_restores_defaults()
    {
        var registry = LoadNew();

        registry.SetExcludedFolders("builtin.claude", [" node_modules ", "NODE_MODULES", "", "bin"]);
        Assert.Equal(["node_modules", "bin"], LoadNew().Find("builtin.claude")!.ExcludedFolders);

        registry.SetExcludedFolders("builtin.claude", null);
        Assert.Null(LoadNew().Find("builtin.claude")!.ExcludedFolders);
    }

    [Fact]
    public void Korean_names_are_written_readably()
    {
        var registry = LoadNew();

        registry.AddFolder("샘플 프로젝트", @"D:\샘플");

        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"샘플 프로젝트\"", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\\샘플", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_changes_all_settings_in_one_commit_or_none()
    {
        var registry = LoadNew();
        var a = registry.AddFolder("A", @"D:\a");
        registry.AddFolder("B", @"D:\b");

        var updated = registry.Update(a.Id, " A2 ", @"D:\a2\", "#00ff00", new VisibilitySettings { ShowAllFiles = true });

        Assert.Equal("A2", updated.Name);
        Assert.Equal(@"D:\a2", updated.Root);
        Assert.Equal("#00FF00", updated.Color);
        Assert.Equal(updated, LoadNew().Find(a.Id));

        // Root clashes with B: name and color must not be saved either.
        Assert.Throws<ArgumentException>(() => registry.Update(a.Id, "A3", @"D:\b", null, VisibilitySettings.Default));
        Assert.Equal(updated, LoadNew().Find(a.Id));
    }

    [Fact]
    public void Update_of_builtin_accepts_only_color_and_visibility()
    {
        var registry = LoadNew();
        var visibility = new VisibilitySettings { DefaultExtensions = [".md"] };

        registry.Update("builtin.claude", null, null, "#123456", visibility);

        var claude = LoadNew().Find("builtin.claude")!;
        Assert.Equal("#123456", claude.Color);
        Assert.Equal(visibility, claude.Visibility);
        Assert.Throws<InvalidOperationException>(() => registry.Update("builtin.claude", "X", null, null, VisibilitySettings.Default));
        Assert.Throws<InvalidOperationException>(() => registry.Update("builtin.claude", null, @"D:\x", null, VisibilitySettings.Default));
    }

    [Fact]
    public void Concurrent_instance_change_is_detected_and_reloaded_instead_of_overwritten()
    {
        var a = LoadNew();
        var b = LoadNew();
        var fromA = a.AddFolder("FromA", @"D:\a");

        Assert.Throws<ProjectsConflictException>(() => b.AddFolder("FromB", @"D:\b"));

        Assert.NotNull(b.Find(fromA.Id));
        Assert.NotNull(LoadNew().Find(fromA.Id));
        var retried = b.AddFolder("FromB", @"D:\b");
        var final = LoadNew();
        Assert.NotNull(final.Find(fromA.Id));
        Assert.NotNull(final.Find(retried.Id));
    }

    [Fact]
    public void Load_continues_read_only_when_defaults_cannot_be_written()
    {
        // A directory at the file path makes both the existence check and the write fail.
        Directory.CreateDirectory(FilePath);
        var registry = CreateRegistry();

        var result = registry.Load();

        Assert.Equal(ProjectRegistryLoadStatus.ReadOnlyUnreadable, result.Status);
        Assert.True(registry.IsReadOnly);
        Assert.Equal(3, registry.Projects.Count);
        Assert.Throws<InvalidOperationException>(() => registry.AddFolder("A", @"D:\a"));
    }

    [Fact]
    public void Load_continues_read_only_when_lock_is_held_elsewhere()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var held = PhysicalFileStore.Instance.AcquireLock(FilePath + ".lock", TimeSpan.FromSeconds(1));

        var result = CreateRegistry().Load();

        Assert.Equal(ProjectRegistryLoadStatus.ReadOnlyUnreadable, result.Status);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Conflict_reload_failure_keeps_previous_list_and_becomes_read_only()
    {
        var registry = LoadNew();
        registry.AddFolder("A", @"D:\a");
        var before = registry.Projects;
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);

        Assert.Throws<ProjectsConflictException>(() => registry.AddFolder("B", @"D:\b"));

        Assert.Same(before, registry.Projects);
        Assert.True(registry.IsReadOnly);
    }

    [Fact]
    public void Invalid_utf8_is_treated_as_corrupt_and_preserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        byte[] bytes = [.. "{\"schemaVersion\":1,\"projects\":[],\"x\":\""u8, 0xC3, 0x28, .. "\"}"u8];
        File.WriteAllBytes(FilePath, bytes);

        var result = CreateRegistry().Load();

        Assert.Equal(ProjectRegistryLoadStatus.RecoveredFromCorrupt, result.Status);
        var corrupt = Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "projects.json.corrupt-*").Single();
        Assert.Equal(bytes, File.ReadAllBytes(corrupt));
    }

    [Fact]
    public void Duplicate_json_keys_are_treated_as_corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"schemaVersion":1,"projects":[],"projects":[]}""");

        Assert.Equal(ProjectRegistryLoadStatus.RecoveredFromCorrupt, CreateRegistry().Load().Status);
    }

    [Fact]
    public void Folder_entry_with_reserved_builtin_id_is_preserved_but_not_loaded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 1, "projects": [
              { "id": "builtin.claude", "kind": "folder", "name": "Mine", "root": "D:\\mine", "order": 0 } ] }
            """);
        var registry = LoadNew();

        registry.SetHidden("builtin.claude", true);

        var claude = registry.Find("builtin.claude")!;
        Assert.True(claude.IsBuiltin);
        var array = JsonNode.Parse(File.ReadAllText(FilePath))!["projects"]!.AsArray();
        Assert.Contains(array, p => (string?)p!["kind"] == "folder" && (string?)p["root"] == @"D:\mine");
    }

    [Fact]
    public void Remove_also_drops_preserved_duplicates_so_project_does_not_reappear()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 1, "projects": [
              { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", "order": 0 },
              { "id": "p1", "kind": "folder", "name": "B", "root": "D:\\b", "order": 1 } ] }
            """);
        var registry = LoadNew();

        registry.Remove("p1");

        Assert.Null(LoadNew().Find("p1"));
    }

    [Theory]
    [InlineData("\"filter\": \"future\"")]
    [InlineData("\"excludedFolders\": { \"x\": 1 }")]
    [InlineData("\"hidden\": \"yes\"")]
    [InlineData("\"order\": \"first\"")]
    [InlineData("\"visibility\": { \"rules\": { \"a\": \"maybe\" } }")]
    [InlineData("\"view\": { \"mode\": \"grid\" }")]
    public void Entry_with_unsupported_known_field_value_is_preserved_verbatim(string field)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $$"""
            { "schemaVersion": 1, "projects": [
              { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", {{field}} } ] }
            """);
        var registry = LoadNew();
        Assert.Null(registry.Find("p1"));

        registry.SetHidden("builtin.codex", true);

        var raw = JsonNode.Parse(File.ReadAllText(FilePath))!["projects"]!.AsArray()
            .Single(p => (string?)p!["id"] == "p1")!.AsObject();
        var expected = JsonNode.Parse($$"""{ {{field}} }""")!.AsObject().Single();
        Assert.True(JsonNode.DeepEquals(expected.Value, raw[expected.Key]));
    }

    [Fact]
    public void Loaded_roots_are_normalized_so_duplicates_are_detected()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 1, "projects": [ { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a\\", "order": 0 } ] }
            """);
        var registry = LoadNew();

        Assert.Equal(@"D:\a", registry.Find("p1")!.Root);
        Assert.Throws<ArgumentException>(() => registry.AddFolder("B", @"D:\a"));
    }

    [Fact]
    public void Extra_fields_cannot_override_known_fields()
    {
        var project = new ProjectDefinition
        {
            Id = "p1",
            Kind = ProjectKind.Folder,
            Name = "A",
            Root = @"D:\a",
            Extra = new JsonObject { ["root"] = @"C:\evil", ["custom"] = 1 },
        };

        var json = ProjectsFileSerializer.Serialize(new ProjectsFileContent(
            [project], [], new JsonObject { ["schemaVersion"] = 99, ["note"] = "x" }));

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(ProjectsFileSerializer.SchemaVersion, (int)root["schemaVersion"]!);
        Assert.Equal("x", (string?)root["note"]);
        var entry = root["projects"]![0]!;
        Assert.Equal(@"D:\a", (string?)entry["root"]);
        Assert.Equal(1, (int)entry["custom"]!);
    }

    [Fact]
    public void Duplicate_ids_in_file_are_preserved_but_loaded_once()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 1, "projects": [
              { "id": "p1", "kind": "folder", "name": "A", "root": "D:\\a", "order": 0 },
              { "id": "p1", "kind": "folder", "name": "B", "root": "D:\\b", "order": 1 } ] }
            """);
        var registry = LoadNew();

        registry.SetHidden("p1", true);

        Assert.Single(registry.Projects, p => p.Id == "p1");
        var array = JsonNode.Parse(File.ReadAllText(FilePath))!["projects"]!.AsArray();
        Assert.Equal(2, array.Count(p => (string?)p!["id"] == "p1"));
    }
}
