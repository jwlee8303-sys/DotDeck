using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Sessions;

public enum AiOutputKind
{
    /// <summary>Session start information (model, working folder).</summary>
    System,

    /// <summary>Assistant text.</summary>
    Text,

    /// <summary>A tool call (name and a short, content-free description).</summary>
    Tool,

    /// <summary>A failed tool call or a refused one (the guard's reason).</summary>
    ToolError,

    /// <summary>The final result line (cost, turns, error state).</summary>
    Result,

    /// <summary>Standard error or a line that is not stream JSON.</summary>
    Raw,
}

/// <summary>One line of the "AI 작업" panel. <see cref="Text"/> is already masked.</summary>
public sealed record AiOutputEvent(AiOutputKind Kind, string Text, bool IsError = false);

/// <summary>
/// Turns <c>claude -p --output-format stream-json --verbose</c> lines into panel lines. Never shows file contents
/// a tool writes (only the path), masks secrets in everything shown, and keeps unknown or broken lines as raw text
/// so nothing is silently lost.
/// </summary>
public static class ClaudeStreamParser
{
    /// <summary>Longest text shown for one event; the rest is cut with a marker.</summary>
    public const int MaxEventLength = 4000;

    public static IReadOnlyList<AiOutputEvent> Parse(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        if (isError)
        {
            return [new AiOutputEvent(AiOutputKind.Raw, Clean(line), IsError: true)];
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return [new AiOutputEvent(AiOutputKind.Raw, Clean(line))];
        }

        return Text(root["type"]) switch
        {
            "system" => System(root),
            "assistant" => Assistant(root),
            "user" => ToolResults(root),
            "result" => [Result(root)],
            _ => [],
        };
    }

    private static IReadOnlyList<AiOutputEvent> System(JsonObject root)
    {
        if (Text(root["subtype"]) != "init")
        {
            return [];
        }

        var model = Text(root["model"]);
        var cwd = Text(root["cwd"]);
        var parts = new List<string> { "세션 시작" };
        if (model is not null)
        {
            parts.Add("모델 " + model);
        }

        if (cwd is not null)
        {
            parts.Add("작업 폴더 " + cwd);
        }

        return [new AiOutputEvent(AiOutputKind.System, Clean(string.Join(" · ", parts)))];
    }

    private static IReadOnlyList<AiOutputEvent> Assistant(JsonObject root)
    {
        var events = new List<AiOutputEvent>();
        foreach (var block in Blocks(root))
        {
            switch (Text(block["type"]))
            {
                case "text" when Text(block["text"]) is { Length: > 0 } text:
                    events.Add(new AiOutputEvent(AiOutputKind.Text, Clean(text)));
                    break;
                case "tool_use":
                    events.Add(new AiOutputEvent(AiOutputKind.Tool, Clean(DescribeTool(Text(block["name"]) ?? "?", block["input"] as JsonObject))));
                    break;
            }
        }

        return events;
    }

    /// <summary>Only failed tool results are shown: successful results may hold whole file contents.</summary>
    private static IReadOnlyList<AiOutputEvent> ToolResults(JsonObject root)
    {
        var events = new List<AiOutputEvent>();
        foreach (var block in Blocks(root))
        {
            if (Text(block["type"]) == "tool_result" && block["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError)
            {
                var content = block["content"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonArray items => string.Join(" ", items.OfType<JsonObject>().Select(i => Text(i["text"])).OfType<string>()),
                    _ => null,
                };
                events.Add(new AiOutputEvent(AiOutputKind.ToolError, Clean(content ?? "도구 실행이 거부되었거나 실패했습니다."), IsError: true));
            }
        }

        return events;
    }

    private static AiOutputEvent Result(JsonObject root)
    {
        var isError = root["is_error"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
        var parts = new List<string> { isError ? "실패" : "완료" };
        if (root["num_turns"] is JsonValue turns && turns.TryGetValue<int>(out var n))
        {
            parts.Add($"{n}턴");
        }

        if (root["total_cost_usd"] is JsonValue cost && cost.TryGetValue<decimal>(out var usd))
        {
            parts.Add("$" + usd.ToString("0.####", CultureInfo.InvariantCulture));
        }

        if (root["duration_ms"] is JsonValue ms && ms.TryGetValue<long>(out var duration))
        {
            parts.Add(TimeSpan.FromMilliseconds(duration).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "초");
        }

        var text = string.Join(" · ", parts);
        if (isError && Text(root["result"]) is { Length: > 0 } message)
        {
            text += Environment.NewLine + message;
        }

        return new AiOutputEvent(AiOutputKind.Result, Clean(text), isError);
    }

    /// <summary>"Edit C:\a\b.md", "Bash: git status", "Grep TODO" — never the content being written.</summary>
    internal static string DescribeTool(string name, JsonObject? input)
    {
        string? Field(string key) => input is null ? null : Text(input[key]);

        var detail = name switch
        {
            "Read" or "Write" or "Edit" or "MultiEdit" => Field("file_path"),
            "NotebookEdit" => Field("notebook_path") ?? Field("file_path"),
            "Glob" => Join(Field("pattern"), Field("path")),
            "Grep" => Join(Field("pattern"), Field("path")),
            "LS" => Field("path"),
            "Bash" => Field("command"),
            "WebFetch" => Field("url"),
            "WebSearch" => Field("query"),
            "TodoWrite" => null,
            "Task" or "Agent" => Field("description"),
            _ => null,
        };

        return detail is null ? name : $"{name} {OneLine(detail)}";
    }

    private static string? Join(string? a, string? b) => (a, b) switch
    {
        (null, null) => null,
        (_, null) => a,
        (null, _) => b,
        _ => $"{a} ({b})",
    };

    private static IEnumerable<JsonObject> Blocks(JsonObject root) =>
        root["message"] is JsonObject message && message["content"] is JsonArray content
            ? content.OfType<JsonObject>()
            : [];

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string OneLine(string text)
    {
        // Mask the whole value first: a cut could otherwise leave part of a secret unmasked.
        var line = SecretMasker.MaskText(text).ReplaceLineEndings(" ");
        return line.Length > 300 ? line[..300] + " …" : line;
    }

    private static string Clean(string text)
    {
        // Mask before cutting (a secret across the cut would otherwise escape the pattern), and again after
        // removing control characters, which could have split an assignment the first pass did not see.
        text = SecretMasker.MaskText(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaxEventLength + 16));
        foreach (var c in text.Length > MaxEventLength ? text[..MaxEventLength] : text)
        {
            // Keep line breaks and tabs; drop other control characters (terminal escapes have no meaning here).
            builder.Append(char.IsControl(c) && c is not '\n' and not '\r' and not '\t' ? ' ' : c);
        }

        if (text.Length > MaxEventLength)
        {
            builder.Append(" … (생략)");
        }

        return SecretMasker.MaskText(builder.ToString());
    }
}
