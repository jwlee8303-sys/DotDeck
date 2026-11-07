using System.Text.Json.Nodes;
using DotDeck.Core.Editors;

namespace DotDeck.Core.Tests.Editors;

/// <summary>Regressions from the Phase 2 review.</summary>
public sealed class ReviewRegressionTests
{
    [Fact]
    public void Hooks_the_form_cannot_show_survive_a_rewrite_and_groups_stay_apart()
    {
        const string Text = """
            {
              "hooks": {
                "FutureEvent": [],
                "PreToolUse": [
                  { "matcher": "Bash", "a": 1, "hooks": [ { "type": "command", "command": "one" }, "raw-item" ] },
                  { "matcher": "Bash", "b": 2, "hooks": [ { "type": "command", "command": "two" } ] },
                  "loose",
                  { "matcher": "X" },
                  { "matcher": "Y", "hooks": [] }
                ]
              }
            }
            """;
        var doc = ClaudeSettingsDocument.TryParse(Text, out _)!;
        var rows = doc.GetHooks().ToList();
        rows[0] = rows[0] with { Command = "one-edited" };

        doc.SetHooks(rows);

        var hooks = JsonNode.Parse(doc.ToJson())!["hooks"]!;
        Assert.NotNull(hooks["FutureEvent"]);
        var pre = hooks["PreToolUse"]!.AsArray();
        Assert.Equal(5, pre.Count);
        Assert.Equal(1, (int)pre[0]!["a"]!);
        Assert.Equal("one-edited", (string?)pre[0]!["hooks"]![0]!["command"]);
        Assert.Equal("raw-item", (string?)pre[0]!["hooks"]![1]);
        Assert.Equal(2, (int)pre[1]!["b"]!);
        Assert.Contains(pre, n => n is JsonValue v && (string?)v == "loose");
        Assert.Contains(pre, n => n is JsonObject g && (string?)g["matcher"] == "X");
    }

    [Theory]
    [InlineData("{\"permissions\": []}")]
    [InlineData("{\"hooks\": \"x\"}")]
    [InlineData("{\"env\": 1}")]
    public void Sections_of_the_wrong_type_block_the_form(string text)
    {
        Assert.Null(ClaudeSettingsDocument.TryParse(text, out var error));
        Assert.Contains("객체가 아닙니다", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_key_keeps_the_comment_after_it()
    {
        var doc = FrontmatterDocument.Parse("---\nname: a\nmodel: sonnet\n# keep\n\ncolor: x\n---\n");

        doc.Set("model", null);

        Assert.Equal("---\nname: a\n# keep\n\ncolor: x\n---\n", doc.ToString());
    }

    [Fact]
    public async Task Timeout_covers_a_hook_that_never_reads_stdin()
    {
        var big = new string('x', 4 * 1024 * 1024);

        var result = await HookRunner.RunAsync("ping -n 30 127.0.0.1 >nul", big, Path.GetTempPath(), TimeSpan.FromMilliseconds(800),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(15));
    }
}
