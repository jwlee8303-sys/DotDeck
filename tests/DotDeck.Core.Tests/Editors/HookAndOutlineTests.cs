using System.Text.Json.Nodes;
using DotDeck.Core.Editors;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Editors;

public sealed class HookAndOutlineTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Hook_gets_stdin_and_reports_output_and_exit_code()
    {
        var script = _temp.Combine("hook.cmd");
        File.WriteAllText(script, "@echo off\r\nmore\r\necho bad 1>&2\r\nexit /b 2\r\n");

        var result = await HookRunner.RunAsync($"\"{script}\"", "{\"a\":1}", _temp.Path, TimeSpan.FromSeconds(20),
            new Dictionary<string, string> { ["DOTDECK_TEST"] = "1" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("{\"a\":1}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("bad", result.StandardError, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
        Assert.Equal("차단(종료 코드 2)", result.Meaning);
    }

    [Fact]
    public async Task Hook_is_killed_after_the_timeout()
    {
        var result = await HookRunner.RunAsync("ping -n 30 127.0.0.1 >nul", string.Empty, _temp.Path, TimeSpan.FromMilliseconds(500),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData("PreToolUse", "Edit|Write", "tool_name", "Edit")]
    [InlineData("PreToolUse", ".*", "tool_name", "Bash")]
    [InlineData("UserPromptSubmit", "", "prompt", "테스트 프롬프트")]
    [InlineData("SessionStart", "", "source", "startup")]
    public void Samples_match_the_event(string evt, string matcher, string key, string value)
    {
        var json = JsonNode.Parse(HookSamples.For(evt, matcher, @"C:\work"))!;

        Assert.Equal(evt, (string?)json["hook_event_name"]);
        Assert.Equal(@"C:\work", (string?)json["cwd"]);
        Assert.Equal(value, (string?)json[key]);
    }

    [Fact]
    public void Outline_skips_frontmatter_and_code_fences()
    {
        const string Text = "---\ntitle: x\n# not heading\n---\n# Title\n\n```bash\n# comment\n```\n## Sub ##\n    # indented code\n#NoSpace\n####### seven\n### Third\r\n";

        var outline = MarkdownOutline.Parse(Text);

        Assert.Equal(
            [new OutlineHeading(1, "Title", 5), new OutlineHeading(2, "Sub", 10), new OutlineHeading(3, "Third", 14)],
            outline);
    }

    [Fact]
    public void Templates_include_built_ins_and_safe_user_files()
    {
        var dir = _temp.Combine("templates");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "b-note.md"), "\uFEFF# note");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, ".env"), "SECRET=1");
        File.WriteAllBytes(Path.Combine(dir, "bin.dat"), [1, 0, 2]);
        File.WriteAllBytes(Path.Combine(dir, "big.md"), new byte[TemplateCatalog.MaxTemplateBytes + 1]);
        Directory.CreateDirectory(Path.Combine(dir, "folder"));

        var templates = TemplateCatalog.Load(PhysicalFileStore.Instance, dir);

        Assert.Equal(TemplateCatalog.BuiltIn.Count + 2, templates.Count);
        Assert.Equal(["a.txt", "b-note.md"], templates.Where(t => !t.IsBuiltIn).Select(t => t.Name));
        Assert.Equal("# note", templates.Single(t => t.Name == "b-note.md").Text);
        Assert.Equal(TemplateCatalog.BuiltIn.Count, TemplateCatalog.Load(PhysicalFileStore.Instance, _temp.Combine("missing")).Count);
    }

    [Fact]
    public void Built_in_templates_pass_their_own_validation()
    {
        var agent = TemplateCatalog.BuiltIn.Single(t => t.FileName == "my-agent.md");
        Assert.Empty(MarkdownAgentValidator.ValidateAgent(@"C:\h\.claude\agents\my-agent.md", agent.Text));
        var skill = TemplateCatalog.BuiltIn.Single(t => t.FileName == "SKILL.md");
        Assert.Empty(MarkdownAgentValidator.ValidateSkill(@"C:\h\.claude\skills\my-skill\SKILL.md", skill.Text));
        var settings = TemplateCatalog.BuiltIn.Single(t => t.FileName == "settings.json");
        Assert.Empty(ClaudeSettingsDocument.TryParse(settings.Text, out _)!.Validate());
    }
}
