using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Mcp;

public interface IAiSessionHost
{
    Task<JsonNode?> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken);
}

/// <summary>One MCP connection, with its own initialization state.</summary>
public sealed class McpServer(IAiSessionHost host)
{
    public const string ServerName = "dotdeck_session";
    private bool _initialized;
    private static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public async Task<string?> HandleAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonNode? id = null;
        try
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(line); }
            catch (JsonException) { return Error(null, -32700, "Parse error"); }
            if (parsed is not JsonObject request || Text(request["jsonrpc"]) != "2.0"
                || Text(request["method"]) is not { } method
                || (request.ContainsKey("id") && request["id"] is not null && !ValidId(request["id"]!))
                || (request["params"] is not null && request["params"] is not JsonObject))
                return Error(null, -32600, "Invalid Request");
            id = request["id"]?.DeepClone();
            if (!request.ContainsKey("id")) return null;
            var parameters = request["params"] as JsonObject ?? new JsonObject();
            JsonNode result;
            switch (method)
            {
                case "initialize":
                    var version = Text(parameters["protocolVersion"]);
                    _initialized = true;
                    result = new JsonObject
                    {
                        ["protocolVersion"] = Versions.Contains(version) ? version : Versions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                    };
                    break;
                case "ping": result = new JsonObject(); break;
                case "tools/list" when _initialized:
                    result = new JsonObject { ["tools"] = Tools() }; break;
                case "tools/call" when _initialized:
                    if (Text(parameters["name"]) is not { } name || !Names.Contains(name))
                        return Error(id, -32602, "Unknown tool");
                    if (parameters["arguments"] is not null && parameters["arguments"] is not JsonObject)
                        return Error(id, -32602, "Invalid arguments");
                    try
                    {
                        var value = await host.CallAsync(name, parameters["arguments"] as JsonObject ?? new(), cancellationToken).ConfigureAwait(false);
                        result = ToolResult(MaskNode(value)?.ToJsonString() ?? "null", false);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        // Exceptions can contain paths or file contents. Return a fixed message.
                        result = ToolResult("요청을 처리할 수 없습니다. 세션 상태·범위·프로젝트 이름과 인수를 확인하세요.", true);
                    }
                    break;
                default: return Error(id, -32601, "Method not found or connection not initialized");
            }
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            var serialized = response.ToJsonString();
            if (serialized.Length > JsonRpcFraming.MaxMessageLength)
            {
                response["result"] = ToolResult("결과가 너무 큽니다. 프로젝트나 파일 패턴을 좁혀 다시 요청하세요.", true);
                serialized = response.ToJsonString();
            }
            return serialized;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return Error(id, -32602, "Invalid params");
        }
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (await JsonRpcFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false) is { } line)
            if (await HandleAsync(line, cancellationToken).ConfigureAwait(false) is { } response)
                await JsonRpcFraming.WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
    }

    public static readonly IReadOnlyList<string> Names = ["list_projects", "get_open_documents", "get_active_document", "find_files", "open_in_editor"];

    private static JsonArray Tools() => new(Names.Select(name => (JsonNode)new JsonObject
    {
        ["name"] = name,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = name != "open_in_editor",
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false,
        },
        ["description"] = name switch
        {
            "list_projects" => "현재 세션 범위의 프로젝트 이름·ID·루트와 표시 규칙",
            "get_open_documents" => "현재 범위 안에서 열려 있는 문서 경로 목록",
            "get_active_document" => "활성 문서 경로·프로젝트·선택 영역(마스킹)",
            "find_files" => "프로젝트 ID 또는 이름과 파일 패턴으로 검색. 표시 필터 적용, 비밀 파일 제외. truncated가 true이면 개수는 일부입니다.",
            _ => "범위 안의 비밀이 아닌 파일을 편집기에서 열기",
        },
        ["inputSchema"] = JsonNode.Parse(name switch
        {
            "find_files" => """{"type":"object","properties":{"project":{"type":"string"},"pattern":{"type":"string"}},"required":["project","pattern"],"additionalProperties":false}""",
            "open_in_editor" => """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            _ => """{"type":"object","properties":{},"additionalProperties":false}""",
        }),
    }).ToArray());

    private static JsonObject ToolResult(string text, bool error) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    // Mask decoded values, before JSON escaping could hide an assignment or known secret.
    private static JsonNode? MaskNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key, MaskNode(kv.Value)))),
        JsonArray array => new JsonArray(array.Select(MaskNode).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var s) => JsonValue.Create(SecretMasker.MaskText(s)),
        _ => node?.DeepClone(),
    };

    private static bool ValidId(JsonNode node) => node is JsonValue v && ((v.TryGetValue<string>(out var id) && id.Length <= 1024) || v.TryGetValue<long>(out _));
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();
}
