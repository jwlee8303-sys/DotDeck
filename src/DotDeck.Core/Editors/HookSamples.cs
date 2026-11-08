using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace DotDeck.Core.Editors;

/// <summary>Sample stdin JSON per hook event, shaped like Claude Code's hook input (editable before a run).</summary>
public static class HookSamples
{
    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string For(string hookEvent, string matcher, string cwd)
    {
        var tool = ToolName(matcher);
        var input = new JsonObject
        {
            ["session_id"] = "dotdeck-test-session",
            ["transcript_path"] = Path.Combine(cwd, ".claude", "transcript.jsonl"),
            ["cwd"] = cwd,
            ["hook_event_name"] = hookEvent,
        };

        switch (hookEvent)
        {
            case "PreToolUse":
                input["tool_name"] = tool;
                input["tool_input"] = ToolInput(tool);
                break;
            case "PostToolUse":
                input["tool_name"] = tool;
                input["tool_input"] = ToolInput(tool);
                input["tool_response"] = new JsonObject { ["success"] = true };
                break;
            case "UserPromptSubmit":
                input["prompt"] = "테스트 프롬프트";
                break;
            case "Notification":
                input["message"] = "Claude needs your permission to use Bash";
                break;
            case "Stop" or "SubagentStop":
                input["stop_hook_active"] = false;
                break;
            case "PreCompact":
                input["trigger"] = "manual";
                input["custom_instructions"] = string.Empty;
                break;
            case "SessionStart":
                input["source"] = "startup";
                break;
            case "SessionEnd":
                input["reason"] = "exit";
                break;
        }

        return input.ToJsonString(s_write);
    }

    // A matcher like "Edit|Write" or "Bash" names tools; regex-like or empty matchers fall back to Bash.
    private static string ToolName(string matcher)
    {
        var first = matcher.Split('|')[0].Trim();
        return first.Length > 0 && first.All(char.IsLetterOrDigit) ? first : "Bash";
    }

    private static JsonObject ToolInput(string tool) => tool switch
    {
        "Bash" => new JsonObject { ["command"] = "git status", ["description"] = "Show working tree status" },
        "Read" => new JsonObject { ["file_path"] = "C:\\example\\README.md" },
        "Write" => new JsonObject { ["file_path"] = "C:\\example\\new.txt", ["content"] = "hello" },
        "Edit" or "MultiEdit" => new JsonObject { ["file_path"] = "C:\\example\\a.txt", ["old_string"] = "a", ["new_string"] = "b" },
        "WebFetch" => new JsonObject { ["url"] = "https://example.com", ["prompt"] = "summarize" },
        _ => [],
    };
}
