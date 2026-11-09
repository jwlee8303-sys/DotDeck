using System.Text.Json.Nodes;
using DotDeck.Core.Explorer;
using DotDeck.Core.Files;
using DotDeck.Core.Settings;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Settings;

public sealed class WordWrapDefaultsTests
{
    [Theory]
    [InlineData(@"C:\h\.claude\CLAUDE.md", true)]
    [InlineData(@"C:\h\NOTES.MD", true)]
    [InlineData(@"C:\h\readme.Txt", true)]
    [InlineData(@"C:\h\.claude\settings.json", false)]
    [InlineData(@"C:\h\.codex\config.toml", false)]
    [InlineData(@"C:\h\a.yaml", false)]
    [InlineData(@"C:\h\Program.cs", false)]
    [InlineData(@"C:\h\run.ps1", false)]
    [InlineData(@"C:\h\View.xaml", false)]
    [InlineData(@"C:\h\script.py", false)]
    [InlineData(@"C:\h\Makefile", false)]
    [InlineData(@"C:\h\LICENSE", false)]
    [InlineData(@"C:\h\notes.md.bak", false)]
    [InlineData(@"C:\h\folder.md\", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Default_list_wraps_prose_only(string? path, bool expected) =>
        Assert.Equal(expected, WordWrapDefaults.IsEnabledFor(path, configured: null));

    [Fact]
    public void Configured_list_replaces_the_preset_and_is_matched_case_insensitively()
    {
        IReadOnlyList<string> configured = ["JSON", "*.Log", ".d.ts"];

        Assert.True(WordWrapDefaults.IsEnabledFor(@"C:\h\a.json", configured));
        Assert.True(WordWrapDefaults.IsEnabledFor(@"C:\h\APP.LOG", configured));
        Assert.True(WordWrapDefaults.IsEnabledFor(@"C:\h\types.d.ts", configured));
        Assert.False(WordWrapDefaults.IsEnabledFor(@"C:\h\a.md", configured));
        Assert.False(WordWrapDefaults.IsEnabledFor(@"C:\h\a.toml", configured));
    }

    [Fact]
    public void An_empty_configured_list_wraps_nothing() =>
        Assert.False(WordWrapDefaults.IsEnabledFor(@"C:\h\a.md", []));
}

public sealed class WordWrapSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Missing_key_or_file_falls_back_to_the_preset()
    {
        var path = _temp.Combine("settings.json");
        var store = new AppSettingsStore(PhysicalFileStore.Instance, path);
        Assert.Null(store.Load().WordWrapExtensions);

        File.WriteAllText(path, "{ \"Theme\": \"Light\" }");
        var settings = store.Load();

        Assert.Null(settings.WordWrapExtensions);
        Assert.Equal(WordWrapDefaults.Preset, WordWrapDefaults.Effective(settings.WordWrapExtensions));
        Assert.True(WordWrapDefaults.IsEnabledFor(@"C:\h\a.md", settings.WordWrapExtensions));
    }

    [Fact]
    public void Extensions_round_trip_normalized()
    {
        var store = new AppSettingsStore(PhysicalFileStore.Instance, _temp.Combine("settings.json"));

        store.Save(new AppSettings { WordWrapExtensions = ["TXT", "*.md", ".log", "bad/one"] });

        Assert.Equal([".log", ".md", ".txt"], store.Load().WordWrapExtensions);
    }

    [Fact]
    public void Unknown_keys_survive_a_load_and_save()
    {
        var path = _temp.Combine("settings.json");
        File.WriteAllText(path, """
            {
              "Theme": "Light",
              "FutureFeature": { "level": 3, "names": ["a", "b"] },
              "AnotherFlag": true
            }
            """);
        var store = new AppSettingsStore(PhysicalFileStore.Instance, path);

        store.Save(store.Load() with { WordWrapExtensions = [".md"] });

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("Light", (string?)root["Theme"]);
        Assert.Equal(3, (int?)root["FutureFeature"]?["level"]);
        Assert.Equal("b", (string?)root["FutureFeature"]?["names"]?[1]);
        Assert.True((bool?)root["AnotherFlag"]);
        Assert.Equal(".md", (string?)root["WordWrapExtensions"]?[0]);
    }
}

public sealed class WordWrapSessionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Only_a_chosen_wrap_is_saved_and_restored()
    {
        var file = _temp.Combine("session.json");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        store.SaveDocuments(
        [
            new(@"C:\h\a.json", 1, WordWrap: true),
            new(@"C:\h\b.md", 1, WordWrap: false),
            new(@"C:\h\c.md", 1),
        ]);

        var docs = store.LoadDocuments();
        Assert.Equal([true, false, null], docs.Select(d => d.WordWrap));
        var saved = JsonNode.Parse(File.ReadAllText(file))!["documents"]!.AsArray();
        Assert.Null(saved[2]!["wordWrap"]);
    }

    [Fact]
    public void A_bad_wrap_value_follows_the_default()
    {
        var file = _temp.Combine("session.json");
        File.WriteAllText(file, """{ "schemaVersion": 1, "documents": [ { "path": "C:\\h\\a.md", "view": 1, "wordWrap": "yes" } ] }""");
        var store = new SessionStateStore(PhysicalFileStore.Instance, file, new ListAppLog());

        Assert.Null(Assert.Single(store.LoadDocuments()).WordWrap);
    }
}
