using System.Text.Json.Nodes;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Sessions;

public static class CodexJsonlParser
{
    public static string? ThreadId(string line)
    {
        var root = ParseObject(line);
        return Text(root?["type"]) == "thread.started" && Guid.TryParse(Text(root?["thread_id"]), out var id) ? id.ToString("D") : null;
    }

    public static string? TerminalFailure(string line)
    {
        var root = ParseObject(line);
        // `error` can describe a recoverable stream retry; only turn.failed is a terminal protocol failure.
        return Text(root?["type"]) == "turn.failed"
            ? Event(AiOutputKind.ToolError, Text((root?["error"] as JsonObject)?["message"]) ?? Text(root?["message"]) ?? "Codex 작업 실패", true).Text
            : null;
    }

    public static IReadOnlyList<AiOutputEvent> Parse(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        var root = isError ? null : ParseObject(line);
        var type = Text(root?["type"]);
        return type switch
        {
            "thread.started" => [Event(AiOutputKind.System, "Codex 세션 시작")],
            "turn.completed" => [Event(AiOutputKind.Result, "Codex 응답 완료 · 실제 파일 변경은 변경 검토에서 확인하세요.")],
            "turn.failed" or "error" => [Event(AiOutputKind.ToolError, Text((root?["error"] as JsonObject)?["message"]) ?? Text(root?["message"]) ?? "Codex 작업 실패", true)],
            "item.completed" => Item(root?["item"] as JsonObject),
            _ => root is null ? [Event(AiOutputKind.Raw, line, isError)] : [],
        };
    }

    private static IReadOnlyList<AiOutputEvent> Item(JsonObject? item)
    {
        var exit = item?["exit_code"] is JsonValue value && value.TryGetValue<int>(out var code) ? code : (int?)null;
        var failed = Text(item?["status"]) is "failed" or "declined" || exit is not (null or 0)
            || item?["error"] is not null;
        var kind = failed ? AiOutputKind.ToolError : AiOutputKind.Tool;
        var diagnostic = failed ? " · " + SafeToolFailure(item) : string.Empty;
        return Text(item?["type"]) switch
        {
            "agent_message" => [Event(AiOutputKind.Text, Text(item?["text"]) ?? string.Empty)],
            "command_execution" => [Event(kind, "명령" + (failed ? " 실패" : string.Empty) + (exit is { } c ? $" (종료 코드 {c})" : string.Empty) + ": " + Text(item?["command"]) + diagnostic, failed)],
            "file_change" => [Event(kind, "파일 변경" + (failed ? " 실패" : string.Empty) + diagnostic, failed)],
            "mcp_tool_call" => [Event(kind, "MCP: " + Text(item?["server"]) + "/" + Text(item?["tool"]) + diagnostic, failed)],
            _ => [],
        };
    }

    private static string SafeToolFailure(JsonObject? item)
    {
        // Successful output and arbitrary command output can be whole file bodies. Classify known errors only.
        var output = Text(item?["aggregated_output"]) ?? Text((item?["error"] as JsonObject)?["message"]) ?? Text(item?["error"]) ?? string.Empty;
        if (output.Contains("read-only", StringComparison.OrdinalIgnoreCase)) return "읽기 전용 경로로 거부되었습니다. 선택한 프로젝트와 Codex 보호 경로를 확인하세요.";
        if (output.Contains("sandbox", StringComparison.OrdinalIgnoreCase)) return "Windows sandbox 실행이 거부되었습니다. sandbox 설정·준비 상태를 확인하세요.";
        if (output.Contains("permission", StringComparison.OrdinalIgnoreCase) || output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)) return "파일 접근 권한 또는 승인이 없어 실행하지 못했습니다.";
        return "도구가 실패했습니다. 뒤따르는 AI 응답과 실행 설정을 확인하세요.";
    }

    private static AiOutputEvent Event(AiOutputKind kind, string text, bool error = false)
    {
        var masked = SecretMasker.MaskText(text);
        var clean = new string(masked.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return new(kind, clean.Length > ClaudeStreamParser.MaxEventLength ? clean[..ClaudeStreamParser.MaxEventLength] + "…" : clean, error);
    }

    private static JsonObject? ParseObject(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
