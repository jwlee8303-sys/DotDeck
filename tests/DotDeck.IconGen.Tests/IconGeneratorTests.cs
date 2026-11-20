using System.Text.Json;
using DotDeck.IconGen;

namespace DotDeck.IconGen.Tests;

public sealed class IconGeneratorTests : IDisposable
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotdeck-icongen-" + Guid.NewGuid().ToString("N"));

    public IconGeneratorTests()
    {
        Directory.CreateDirectory(Icons);
        Svg("file", "<path d=\"M3 1h7l3 3v11H3z\" fill=\"#90a4ae\"/>");
        Svg("folder", "<path d=\"M1 3h5l2 2h7v9H1z\" fill=\"#90a4ae\"/>");
        Svg("folder-open", "<path d=\"M1 3h5l2 2h7v2H4l-3 7z\" fill=\"#90a4ae\"/>");
        Svg("brand", "<circle cx=\"8\" cy=\"8\" r=\"7\" fill=\"#d97757\"/><path d=\"M4 8h8\" stroke=\"#fff\"/>");
        Svg("folder-brand-open", "<path d=\"M1 3h14v10H1z\" fill=\"#10a37f\"/>");
        Svg("markdown", "<path d=\"M1 4h14v8H1z\" fill=\"#42a5f5\"/>");
        Svg("broken", "<path d=\"M1 1h1\" filter=\"url(#f)\" fill=\"#e53935\"/>");
        Manifest("file", "folder", "folder-open", "brand", "folder-brand-open", "markdown", "broken");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Icons => Path.Combine(_root, "icons");

    private string ManifestPath => Path.Combine(_root, "material-icons.json");

    private string Out => Path.Combine(_root, "out");

    private void Svg(string key, string body) =>
        File.WriteAllText(Path.Combine(Icons, key + ".svg"), $"<svg {Ns} viewBox=\"0 0 16 16\">{body}</svg>");

    private void Manifest(params string[] keys) => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
    {
        iconDefinitions = keys.ToDictionary(k => k, k => new { iconPath = $"./../icons/{k}.svg" }),
    }));

    private string Exclude(string json)
    {
        var path = Path.Combine(_root, "exclude.json");
        File.WriteAllText(path, json);
        return path;
    }

    private GenerateResult Run(string? exclude = null, string? extra = null) =>
        IconGenerator.Generate(new GenerateOptions(Icons, ManifestPath, exclude, extra, Out), Program.ValidateWithWpfOnSta);

    private string Output(string key) => File.ReadAllText(Path.Combine(Out, key + ".xaml"));

    [Fact]
    public void Empty_exclude_list_converts_everything_and_replaces_failures_with_a_generic_icon()
    {
        var result = Run(Exclude("""{ "icons": [] }"""));

        Assert.Equal(6, result.Converted);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("broken", failure.Key);
        Assert.Empty(result.Excluded);
        Assert.Contains("#FFD97757", Output("brand"), StringComparison.Ordinal);
        // The stand-in is Material's generic file in the failed icon's color.
        Assert.Contains("#FFE53935", Output("broken"), StringComparison.Ordinal);
        Assert.Contains("M 3 1 h 7 l 3 3", Output("broken"), StringComparison.Ordinal);
    }

    [Fact]
    public void Excluded_icons_become_generic_icons_of_the_same_color_family()
    {
        var result = Run(Exclude("""
            { "icons": [
              { "key": "brand", "reason": "logo" },
              { "key": "folder-brand-open", "reason": "logo" }
            ] }
            """));

        Assert.Equal(["brand", "folder-brand-open"], result.Excluded.Select(e => e.Key));
        var brand = Output("brand");
        Assert.Contains("#FFD97757", brand, StringComparison.Ordinal);
        Assert.Contains("M 3 1 h 7 l 3 3", brand, StringComparison.Ordinal); // generic file shape
        Assert.DoesNotContain("EllipseGeometry", brand, StringComparison.Ordinal); // logo artwork is gone
        var folder = Output("folder-brand-open");
        Assert.Contains("#FF10A37F", folder, StringComparison.Ordinal);
        Assert.Contains("M 1 3 h 5 l 2 2 h 7 v 2 H 4", folder, StringComparison.Ordinal); // generic open folder
    }

    [Fact]
    public void Replace_with_uses_another_icons_artwork()
    {
        Run(Exclude("""{ "icons": [ { "key": "brand", "reason": "logo", "replaceWith": "markdown" } ] }"""));
        Assert.Equal(Output("markdown"), Output("brand"));
    }

    [Theory]
    [InlineData("""{ "icons": [ { "key": "nope", "reason": "x" } ] }""", "not a Material icon")]
    [InlineData("""{ "icons": [ { "key": "brand" } ] }""", "needs a reason")]
    [InlineData("""{ "icons": [ { "key": "brand", "reason": "x", "replaceWith": "nope" } ] }""", "replaceWith")]
    [InlineData("""{ "icons": [ { "key": "file", "reason": "x" } ] }""", "generic icon 'file'")]
    public void Invalid_exclude_lists_stop_generation(string json, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run(Exclude(json)));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Out));
    }

    [Fact]
    public void Running_twice_is_byte_identical_and_removes_stale_files()
    {
        Run();
        File.WriteAllText(Path.Combine(Out, "stale.xaml"), "x");
        File.WriteAllText(Path.Combine(Out, "keep.txt"), "x");
        var first = Directory.GetFiles(Out, "*.xaml").Where(f => !f.EndsWith("stale.xaml", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileName(f), f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));

        var result = Run();

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(Out, "stale.xaml")));
        Assert.True(File.Exists(Path.Combine(Out, "keep.txt")));
        foreach (var (name, (bytes, time)) in first)
        {
            var path = Path.Combine(Out, name);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(time, File.GetLastWriteTimeUtc(path)); // unchanged files are not rewritten
        }
    }

    [Fact]
    public void DotDeck_icons_are_added_and_may_not_shadow_material_keys()
    {
        var extra = Path.Combine(_root, "dotdeck");
        Directory.CreateDirectory(extra);
        File.WriteAllText(Path.Combine(extra, "dotdeck-claude.svg"), $"<svg {Ns} viewBox=\"0 0 16 16\"><circle cx=\"8\" cy=\"8\" r=\"6\" fill=\"#e8743b\"/></svg>");

        var result = Run(extra: extra);

        Assert.Equal(["dotdeck-claude"], result.Extra);
        Assert.Contains("EllipseGeometry", Output("dotdeck-claude"), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(extra, "markdown.svg"), File.ReadAllText(Path.Combine(extra, "dotdeck-claude.svg")));
        Assert.Throws<InvalidOperationException>(() => Run(extra: extra));
    }

    [Theory]
    [InlineData("./../icons/../../markdown.svg")]
    [InlineData("./../other/markdown.svg")]
    [InlineData("C:/x/markdown.svg")]
    public void Manifest_paths_outside_the_icons_folder_are_rejected(string iconPath)
    {
        // The file name exists in the icons folder, so only the path itself can be the reason.
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { iconDefinitions = new Dictionary<string, object>
        {
            ["file"] = new { iconPath = "./../icons/file.svg" },
            ["folder"] = new { iconPath = "./../icons/folder.svg" },
            ["folder-open"] = new { iconPath = "./../icons/folder-open.svg" },
            ["x"] = new { iconPath },
        } }));
        var ex = Assert.Throws<InvalidOperationException>(() => Run());
        Assert.Contains("unexpected icon definition 'x'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Markdown")] // differs only in case from an existing key
    [InlineData("con")]
    [InlineData("nul.x")]
    public void Keys_that_cannot_be_distinct_windows_files_are_rejected(string key)
    {
        Svg(key == "Markdown" ? "markdown2" : "dev", "<path d=\"M1 1h1\"/>");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(File.ReadAllText(ManifestPath))!;
        manifest["iconDefinitions"][key] = new() { ["iconPath"] = $"./../icons/{(key == "Markdown" ? "markdown2" : "dev")}.svg" };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));

        Assert.Throws<InvalidOperationException>(() => Run());
    }

    [Theory]
    [InlineData("<path fill=\"#ffffff\"/><path fill=\"#d97757\"/><path fill=\"#000\"/>", "#D97757")]
    [InlineData("<path fill=\"#fff\"/>", "#90A4AE")]
    public void Main_color_ignores_neutral_colors(string body, string expected) =>
        Assert.Equal(expected, IconGenerator.MainColor(body));
}
