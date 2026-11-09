using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.Core.Tests.AiCli;

public sealed class ClaudeStreamParserTests
{
    private static string Line(JsonObject json) => json.ToJsonString();

    private static JsonObject Assistant(params JsonObject[] blocks) => new()
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["content"] = new JsonArray([.. blocks]) },
    };

    [Fact]
    public void Init_line_shows_model_and_folder()
    {
        var events = ClaudeStreamParser.Parse(Line(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "init", ["model"] = "claude-opus-5-5", ["cwd"] = @"C:\work\web",
        }), isError: false);

        var e = Assert.Single(events);
        Assert.Equal(AiOutputKind.System, e.Kind);
        Assert.Contains("claude-opus-5-5", e.Text, StringComparison.Ordinal);
        Assert.Contains(@"C:\work\web", e.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assistant_text_and_tool_calls_are_shown_without_written_content()
    {
        var events = ClaudeStreamParser.Parse(Line(Assistant(
            new JsonObject { ["type"] = "text", ["text"] = "README를 정리하겠습니다." },
            new JsonObject
            {
                ["type"] = "tool_use", ["name"] = "Write",
                ["input"] = new JsonObject { ["file_path"] = @"C:\work\web\README.md", ["content"] = "WRITTEN-BODY-123" },
            },
            new JsonObject
            {
                ["type"] = "tool_use", ["name"] = "Bash",
                ["input"] = new JsonObject { ["command"] = "git status\ngit diff" },
            })), isError: false);

        Assert.Equal(
            [(AiOutputKind.Text, "README를 정리하겠습니다."), (AiOutputKind.Tool, @"Write C:\work\web\README.md"), (AiOutputKind.Tool, "Bash git status git diff")],
            events.Select(e => (e.Kind, e.Text)));
        Assert.DoesNotContain(events, e => e.Text.Contains("WRITTEN-BODY-123", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_failed_tool_results_are_shown()
    {
        JsonObject Result(bool isError, string content) => new()
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["is_error"] = isError, ["content"] = content }),
            },
        };

        Assert.Empty(ClaudeStreamParser.Parse(Line(Result(false, "file body that must not show")), isError: false));
        var refused = Assert.Single(ClaudeStreamParser.Parse(Line(Result(true, "DotDeck 범위 밖 경로입니다")), isError: false));
        Assert.Equal(AiOutputKind.ToolError, refused.Kind);
        Assert.True(refused.IsError);
        Assert.Contains("범위 밖", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_line_summarizes_cost_turns_and_errors()
    {
        var ok = Assert.Single(ClaudeStreamParser.Parse(Line(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["num_turns"] = 3, ["total_cost_usd"] = 0.0421m, ["duration_ms"] = 12500,
        }), isError: false));
        Assert.Equal((AiOutputKind.Result, "완료 · 3턴 · $0.0421 · 12.5초", false), (ok.Kind, ok.Text, ok.IsError));

        var failed = Assert.Single(ClaudeStreamParser.Parse(Line(new JsonObject
        {
            ["type"] = "result", ["is_error"] = true, ["result"] = "Credit balance is too low",
        }), isError: false));
        Assert.True(failed.IsError);
        Assert.Contains("Credit balance", failed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_lines_and_stderr_are_kept_as_raw_text_with_secrets_masked()
    {
        var raw = Assert.Single(ClaudeStreamParser.Parse("{not json API_KEY=fake-value-937451", isError: false));
        Assert.Equal(AiOutputKind.Raw, raw.Kind);
        Assert.DoesNotContain("fake-value-937451", raw.Text, StringComparison.Ordinal);

        var error = Assert.Single(ClaudeStreamParser.Parse("warning: \u001b[31mred\u001b[0m", isError: true));
        Assert.True(error.IsError);
        Assert.DoesNotContain("\u001b", error.Text, StringComparison.Ordinal);

        Assert.Empty(ClaudeStreamParser.Parse("   ", isError: false));
        Assert.Empty(ClaudeStreamParser.Parse("{\"type\":\"stream_event\"}", isError: false));
    }

    [Fact]
    public void Secrets_across_the_cut_are_masked_before_cutting()
    {
        var text = new string('a', ClaudeStreamParser.MaxEventLength - 12) + " API_KEY=fake-boundary-secret-445566";
        var command = new string('x', 290) + " API_KEY=fake-command-secret-778899";

        var events = ClaudeStreamParser.Parse(Line(Assistant(
            new JsonObject { ["type"] = "text", ["text"] = text },
            new JsonObject { ["type"] = "tool_use", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } })), isError: false);

        Assert.DoesNotContain("fake-bo", events[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-co", events[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_in_assistant_text_are_masked_and_long_text_is_cut()
    {
        var events = ClaudeStreamParser.Parse(Line(Assistant(
            new JsonObject { ["type"] = "text", ["text"] = "설정: OPENAI_API_KEY=fake-secret-118822" },
            new JsonObject { ["type"] = "text", ["text"] = new string('가', ClaudeStreamParser.MaxEventLength + 50) })), isError: false);

        Assert.DoesNotContain("fake-secret-118822", events[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("(생략)", events[1].Text, StringComparison.Ordinal);
        Assert.True(events[1].Text.Length < ClaudeStreamParser.MaxEventLength + 20);
    }
}
