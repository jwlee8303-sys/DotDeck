using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Context;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Presets;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class SessionPresetStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.Combine("roaming", "sessions.json");

    private SessionPresetStore NewStore()
    {
        var store = new SessionPresetStore(PhysicalFileStore.Instance, FilePath, _time);
        store.Load();
        return store;
    }

    private static SessionPreset Preset(string id = "p1") => new()
    {
        Id = id,
        Name = "웹 작업",
        PrimaryProjectId = "a",
        AdditionalProjectIds = ["b", "c"],
        Cli = CliKind.Codex,
        Mode = LaunchMode.NonInteractive,
        IncludeOpenDocuments = false,
        ResumeConversation = true,
        AllowReadOnlyShell = true,
    };

    [Fact]
    public void Missing_file_loads_empty()
    {
        var store = new SessionPresetStore(PhysicalFileStore.Instance, FilePath, _time);

        Assert.Equal(PresetStoreLoadStatus.Missing, store.Load());
        Assert.Empty(store.Presets);
    }

    [Fact]
    public void Presets_selections_and_conversations_round_trip()
    {
        var store = NewStore();
        store.SavePreset(Preset());
        store.SetLastAdditional("a", ["b", "a", "b", "c"]);
        store.SetConversation("key1", CliKind.Claude, "3f2a6c1e-1111-4222-8333-944455556666");
        store.SetConversation("key1", CliKind.Codex, "thread-1");

        var reloaded = NewStore();

        Assert.Equal(PresetStoreLoadStatus.Loaded, reloaded.Status);
        var preset = Assert.Single(reloaded.Presets);
        var expected = Preset();
        Assert.Equal(expected, preset with { AdditionalProjectIds = expected.AdditionalProjectIds });
        Assert.Equal(["b", "c"], preset.AdditionalProjectIds);
        Assert.Equal(["b", "c"], reloaded.GetLastAdditional("a"));
        Assert.Equal(new ConversationIds("3f2a6c1e-1111-4222-8333-944455556666", "thread-1"), reloaded.GetConversation("key1"));
        Assert.Contains("웹 작업", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_with_the_same_id_replaces_and_remove_deletes()
    {
        var store = NewStore();
        store.SavePreset(Preset());
        store.SavePreset(Preset() with { Name = "이름 변경" });
        store.SavePreset(Preset("p2"));
        store.RemovePreset("p1");

        Assert.Equal(["p2"], NewStore().Presets.Select(p => p.Id));
    }

    [Fact]
    public void Forgetting_both_conversation_ids_removes_the_entry()
    {
        var store = NewStore();
        store.SetConversation("k", CliKind.Claude, "x");
        store.SetConversation("k", CliKind.Claude, null);

        Assert.Null(NewStore().GetConversation("k"));
    }

    [Fact]
    public void Unknown_fields_and_unreadable_presets_are_kept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            {
              "schemaVersion": 1,
              "future": { "x": 1 },
              "presets": [
                { "id": "p1", "name": "A", "primary": "a", "cli": "claude", "color": "red" },
                { "id": "p2", "name": "B", "primary": "a", "cli": "gemini" },
                { "id": "p1", "name": "dup", "primary": "a" }
              ]
            }
            """);
        var store = NewStore();

        store.SavePreset(Preset("p3"));

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(1, (int)json["future"]!["x"]!);
        var presets = json["presets"]!.AsArray();
        Assert.Equal("red", (string?)presets.First(p => (string?)p!["id"] == "p1")!["color"]);
        Assert.Contains(presets, p => (string?)p!["cli"] == "gemini");
        Assert.Contains(presets, p => (string?)p!["name"] == "dup");
        Assert.Equal(["p1", "p3"], NewStore().Presets.Select(p => p.Id));
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_replaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var store = new SessionPresetStore(PhysicalFileStore.Instance, FilePath, _time);

        Assert.Equal(PresetStoreLoadStatus.RecoveredFromCorrupt, store.Load());
        Assert.Equal("{ not json", File.ReadAllText(store.CorruptCopyPath!));
        Assert.EndsWith(".corrupt-20261109-100000", store.CorruptCopyPath, StringComparison.Ordinal);

        store.SavePreset(Preset());
        Assert.Single(NewStore().Presets);
    }

    [Fact]
    public void Newer_schema_is_never_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "schemaVersion": 99, "presets": [] }""");
        var store = new SessionPresetStore(PhysicalFileStore.Instance, FilePath, _time);

        Assert.Equal(PresetStoreLoadStatus.ReadOnly, store.Load());
        Assert.Throws<InvalidOperationException>(() => store.SavePreset(Preset()));
        Assert.Contains("99", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_is_treated_as_corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, [0x7B, 0xFF, 0xFE, 0x7D]);
        var store = new SessionPresetStore(PhysicalFileStore.Instance, FilePath, _time);

        Assert.Equal(PresetStoreLoadStatus.RecoveredFromCorrupt, store.Load());
        Assert.Equal([0x7B, 0xFF, 0xFE, 0x7D], File.ReadAllBytes(store.CorruptCopyPath!));
    }

    [Fact]
    public void Editing_with_a_fresh_object_keeps_unknown_preset_fields()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "schemaVersion": 1, "presets": [ { "id": "p1", "name": "A", "primary": "a", "color": "red" } ] }""");
        var store = NewStore();

        store.SavePreset(Preset() with { Name = "새 이름" });

        var saved = JsonNode.Parse(File.ReadAllText(FilePath))!["presets"]![0]!;
        Assert.Equal("새 이름", (string?)saved["name"]);
        Assert.Equal("red", (string?)saved["color"]);
    }

    [Fact]
    public void Removing_a_preset_also_removes_its_duplicates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            { "schemaVersion": 1, "presets": [
              { "id": "p1", "name": "A", "primary": "a" },
              { "id": "p1", "name": "dup", "primary": "a" },
              { "id": "p2", "name": "B", "primary": "a", "cli": "gemini" } ] }
            """);
        var store = NewStore();

        store.RemovePreset("p1");

        Assert.Empty(NewStore().Presets);
        Assert.DoesNotContain("dup", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Contains("gemini", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_presets_cannot_be_changed_through_references()
    {
        var ids = new List<string> { "b" };
        var store = NewStore();
        store.SavePreset(Preset() with { AdditionalProjectIds = ids, Extra = new JsonObject { ["x"] = 1 } });

        ids.Add("zzz");
        store.Presets[0].Extra!["x"] = 2;

        Assert.Equal(["b"], store.Presets[0].AdditionalProjectIds);
        Assert.Equal(1, (int)store.Presets[0].Extra!["x"]!);
    }

    [Fact]
    public void Saves_from_two_instances_do_not_overwrite_each_other()
    {
        var first = NewStore();
        var second = NewStore();

        first.SavePreset(Preset("p1"));
        second.SavePreset(Preset("p2"));
        first.SetLastAdditional("a", ["b"]);

        var reloaded = NewStore();
        Assert.Equal(["p1", "p2"], reloaded.Presets.Select(p => p.Id));
        Assert.Equal(["b"], reloaded.GetLastAdditional("a"));
    }

    [Fact]
    public void File_upgraded_by_a_newer_version_after_loading_is_not_overwritten()
    {
        var store = NewStore();
        File.WriteAllText(FilePath, """{ "schemaVersion": 99 }""");

        Assert.Throws<InvalidOperationException>(() => store.SavePreset(Preset()));
        Assert.Equal("""{ "schemaVersion": 99 }""", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Resolve_uses_ids_so_renames_apply_and_missing_projects_warn()
    {
        ProjectDefinition[] projects =
        [
            new() { Id = "a", Kind = ProjectKind.Folder, Name = "새 이름", Root = @"C:\new" },
            new() { Id = "b", Kind = ProjectKind.Folder, Name = "B", Root = @"C:\b" },
        ];

        var ok = SessionPresetStore.Resolve(Preset(), projects);
        Assert.True(ok.CanLaunch);
        Assert.Equal(["c"], ok.MissingProjectIds);
        Assert.True(ok.HasWarning);

        var broken = SessionPresetStore.Resolve(Preset() with { PrimaryProjectId = "gone" }, projects);
        Assert.False(broken.CanLaunch);
        Assert.Contains("gone", broken.MissingProjectIds);
    }
}

public sealed class ContextDocumentBuilderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SessionScope _scope;
    private readonly ProjectDefinition[] _projects;
    private readonly string _main;
    private readonly string _web;

    public ContextDocumentBuilderTests()
    {
        _main = _temp.Combine("DotDeck");
        _web = _temp.Combine("web");
        Directory.CreateDirectory(_main);
        Directory.CreateDirectory(_web);
        Directory.CreateDirectory(_temp.Combine("outside"));
        var paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("r"), _temp.Combine("l"));
        _projects =
        [
            new() { Id = "a", Kind = ProjectKind.Folder, Name = "DotDeck", Root = _main, ExcludedFolders = ["bin", "obj"] },
            new()
            {
                Id = "b", Kind = ProjectKind.Folder, Name = "웹|게임\n# 주입", Root = _web,
                Visibility = new VisibilitySettings { ShowAllFiles = false, DefaultExtensions = [".md", ".json"] },
            },
        ];
        _scope = SessionScope.Create("a", ["b"], _projects, paths, PhysicalFileStore.Instance).Scope!;
    }

    public void Dispose() => _temp.Dispose();

    private ContextInput Input(params string[] open) => new()
    {
        Scope = _scope,
        Projects = _projects,
        OpenDocuments = open,
        ActiveDocument = open.FirstOrDefault(),
    };

    [Fact]
    public void Lists_scope_projects_rules_and_filters()
    {
        var text = ContextDocumentBuilder.Build(Input());

        Assert.Contains($"| DotDeck | {_main} | 주 프로젝트(작업 폴더) |", text, StringComparison.Ordinal);
        Assert.Contains(@"| 웹\|게임 # 주입 |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n# 주입", text, StringComparison.Ordinal);
        Assert.Contains("이름이 모호하면 후보를 보여주고 물어봅니다", text, StringComparison.Ordinal);
        Assert.Contains("활성 문서가 속한 프로젝트를 우선", text, StringComparison.Ordinal);
        Assert.Contains("제외 폴더 없음", text, StringComparison.Ordinal);
        Assert.DoesNotContain("제외 폴더 bin, obj", text, StringComparison.Ordinal);
        Assert.Contains("표시 .md, .json", text, StringComparison.Ordinal);
        Assert.Contains("범위 안에 열린 문서가 없습니다", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_documents_exclude_secret_files_and_paths_outside_the_scope()
    {
        var active = Path.Combine(_web, "README.md");
        var text = ContextDocumentBuilder.Build(Input(
            active,
            Path.Combine(_main, "src", "a.cs"),
            Path.Combine(_main, ".env.local"),
            Path.Combine(_main, "keys", "server.pem"),
            Path.Combine(_temp.Combine("outside"), "notes.md"),
            "relative.md"));

        Assert.Contains($"- 활성 문서: {active} (웹|게임 # 주입)", text, StringComparison.Ordinal);
        Assert.Contains($"- {Path.Combine(_main, "src", "a.cs")} (DotDeck)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", text.Replace(".env 계열", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("server.pem", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", text, StringComparison.Ordinal);
        Assert.DoesNotContain("relative.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_dot_paths_are_normalized_before_the_scope_check()
    {
        var sneaky = Path.Combine(_main, "..", "outside", "notes.md");
        var inside = Path.Combine(_main, "sub", "..", "b.md");

        var text = ContextDocumentBuilder.Build(Input(inside, sneaky));

        Assert.DoesNotContain("outside", text, StringComparison.Ordinal);
        Assert.Contains($"- 활성 문서: {Path.Combine(_main, "b.md")} (DotDeck)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("..", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_paths_become_spaces()
    {
        var text = ContextDocumentBuilder.Build(Input(Path.Combine(_main, "a\u0001\tb.md")));

        Assert.Contains("a  b.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', text);
        Assert.DoesNotContain('\t', text);
    }

    [Fact]
    public void Secret_active_document_is_not_named()
    {
        var text = ContextDocumentBuilder.Build(Input(Path.Combine(_main, "auth.json")));

        Assert.DoesNotContain("활성 문서:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(_main, "auth.json"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_list_can_be_turned_off()
    {
        var text = ContextDocumentBuilder.Build(Input(Path.Combine(_main, "a.md")) with { IncludeOpenDocuments = false });

        Assert.DoesNotContain("## 열린 문서", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_document_lists_are_truncated_and_length_is_capped()
    {
        var many = Enumerable.Range(0, 200).Select(i => Path.Combine(_main, $"file{i:D3}.md")).ToArray();

        var full = ContextDocumentBuilder.Build(Input(many), maxLength: 100_000);
        Assert.Contains("… 외 159개", full, StringComparison.Ordinal);

        var capped = ContextDocumentBuilder.Build(Input(many), maxLength: 1_000);
        Assert.True(capped.Length <= 1_000, $"{capped.Length}");
        Assert.Contains("일부를 생략했습니다", capped, StringComparison.Ordinal);
    }
}
