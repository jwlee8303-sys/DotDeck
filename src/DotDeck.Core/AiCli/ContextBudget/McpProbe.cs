using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>A started MCP server process (stdin and stdout pipes) whose whole process tree ends on dispose.</summary>
public interface IMcpProcess : IDisposable
{
    Stream Input { get; }

    Stream Output { get; }
}

/// <summary>Starts a measured server (App: a kill-on-close Job Object, no window, stderr discarded).</summary>
public interface IMcpProcessHost
{
    /// <param name="executable">Fully qualified program (cmd.exe for .cmd/.bat launchers).</param>
    /// <param name="commandLine">Complete command line, program included.</param>
    /// <param name="environment">Added to the inherited environment (the server's configured env, passed through as is).</param>
    IMcpProcess Start(string executable, string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment);
}

/// <summary>Outcome of one measurement. <paramref name="Reason"/> is a short Korean text without configuration values.</summary>
public sealed record McpProbeResult(ContextItemState State, int ToolCount, int Tokens, string? Reason)
{
    public static McpProbeResult Fail(string reason) => new(ContextItemState.Failed, 0, 0, reason);

    public static McpProbeResult AuthRequired(string reason = "인증 필요") => new(ContextItemState.AuthRequired, 0, 0, reason);
}

/// <summary>
/// Measures an MCP server: <c>initialize</c>, <c>notifications/initialized</c> and <c>tools/list</c> (all pages), then
/// ends it right away. Stdio servers run through <see cref="IMcpProcessHost"/>; remote servers are called over
/// Streamable HTTP or the legacy SSE transport. Servers that need an OAuth login are not contacted (or are reported as
/// "인증 필요" when they answer 401/403). Nothing of the configuration, the server's output or its errors is logged.
/// </summary>
public sealed class McpProbe(IMcpProcessHost? processes, HttpMessageHandler? http = null, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public const string ProtocolVersion = "2025-06-18";
    private const int MaxPages = 20;
    private const int MaxLineChars = 16 * 1024 * 1024;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<McpProbeResult> MeasureAsync(McpServerSpec server, string? defaultWorkingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Unsupported is { } unsupported)
        {
            return McpProbeResult.Fail(unsupported);
        }

        if (server.UsesOAuth)
        {
            return McpProbeResult.AuthRequired("인증 필요 (OAuth 로그인 서버)");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            return server.Transport == McpTransport.Stdio
                ? await MeasureStdioAsync(server, defaultWorkingDirectory, limit.Token).ConfigureAwait(false)
                : await MeasureRemoteAsync(server, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            // Ending the process at the deadline also closes its pipes: whatever failed after that is the timeout.
            return McpProbeResult.Fail(string.Create(CultureInfo.InvariantCulture, $"{_timeout.TotalSeconds:0}초 안에 응답 없음"));
        }
        catch (McpProbeException ex)
        {
            return ex.Result;
        }
        catch (HttpRequestException ex)
        {
            return McpProbeResult.Fail(ex.StatusCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)code}")
                : "연결 실패");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return McpProbeResult.Fail(ex is JsonException ? "응답 형식 오류" : "실행 또는 통신 실패");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // An env name the OS rejects, a malformed endpoint: reported without the values.
            return McpProbeResult.Fail("설정 값 형식 오류");
        }
    }

    // ---------------------------------------------------------------- stdio

    private async Task<McpProbeResult> MeasureStdioAsync(McpServerSpec server, string? defaultWorkingDirectory, CancellationToken token)
    {
        if (processes is null)
        {
            return McpProbeResult.Fail("이 환경에서 서버를 실행할 수 없음");
        }

        var launch = McpCommandResolver.Resolve(server);
        if (launch.Error is { } error)
        {
            return McpProbeResult.Fail(error);
        }

        var cwd = server.WorkingDirectory is { } configured && Path.IsPathFullyQualified(configured) && Directory.Exists(configured)
            ? configured
            : defaultWorkingDirectory is { } folder && Directory.Exists(folder) ? folder : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        using var process = processes.Start(launch.Executable!, launch.CommandLine!, cwd, server.Environment);

        // Disposing the process (kill + closed pipes) is what unblocks a read that never ends.
        using var registration = token.Register(process.Dispose);
        var writer = new StreamWriter(process.Input, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(process.Output, new UTF8Encoding(false));
        var session = new StdioSession(writer, reader);
        return await Task.Run(() => RunAsync(session, token), token).ConfigureAwait(false);
    }

    private sealed class StdioSession(StreamWriter writer, StreamReader reader) : IMcpSession
    {
        public async Task SendAsync(JsonObject message, CancellationToken token) =>
            await writer.WriteLineAsync(message.ToJsonString().AsMemory(), token).ConfigureAwait(false);

        public async Task<JsonObject> ReceiveAsync(int id, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false)
                    ?? throw new McpProbeException(McpProbeResult.Fail("서버가 응답 전에 종료됨"));
                if (line.Length > MaxLineChars)
                {
                    throw new McpProbeException(McpProbeResult.Fail("응답이 너무 큼"));
                }

                if (Parse(line) is not { } message)
                {
                    continue; // log lines some servers print on stdout
                }

                if (await AnswerServerRequestAsync(message, this, token).ConfigureAwait(false))
                {
                    continue;
                }

                if (message["id"] is JsonValue value && value.TryGetValue<int>(out var got) && got == id)
                {
                    return message;
                }
            }
        }
    }

    // ---------------------------------------------------------------- remote

    private async Task<McpProbeResult> MeasureRemoteAsync(McpServerSpec server, CancellationToken token)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return McpProbeResult.Fail("URL 형식 오류");
        }

        // No redirects: custom headers (API keys) would follow them to another host.
        using var client = http is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(http, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        IMcpSession session = server.Transport == McpTransport.Sse
            ? await SseSession.OpenAsync(client, uri, server.Headers, token).ConfigureAwait(false)
            : new HttpSession(client, uri, server.Headers);
        try
        {
            return await RunAsync(session, token).ConfigureAwait(false);
        }
        finally
        {
            (session as IDisposable)?.Dispose();
            if (session is HttpSession { SessionId: not null } streamable)
            {
                await streamable.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new McpProbeException(McpProbeResult.AuthRequired());
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new McpProbeException(McpProbeResult.Fail(string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}")));
        }
    }

    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>Streamable HTTP: every message is a POST; the answer is JSON or an event stream.</summary>
    private sealed class HttpSession(HttpClient client, Uri uri, IReadOnlyDictionary<string, string> headers) : IMcpSession
    {
        private JsonObject? _pending;

        public string? SessionId { get; private set; }

        public async Task SendAsync(JsonObject message, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            if (SessionId is not null)
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
            }

            AddHeaders(request, headers);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            ThrowForStatus(response);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            {
                SessionId = ids.FirstOrDefault();
            }

            if (message["id"] is null || response.StatusCode == HttpStatusCode.Accepted)
            {
                return;
            }

            var id = message["id"]!.GetValue<int>();
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                await foreach (var data in ReadEvents(stream, token).ConfigureAwait(false))
                {
                    if (Parse(data.Data) is { } reply && reply["id"] is JsonValue value && value.TryGetValue<int>(out var got) && got == id)
                    {
                        _pending = reply;
                        return;
                    }
                }

                throw new McpProbeException(McpProbeResult.Fail("서버가 응답 없이 연결을 닫음"));
            }

            using var text = new StreamReader(stream, Encoding.UTF8);
            _pending = Parse(await text.ReadToEndAsync(token).ConfigureAwait(false))
                ?? throw new McpProbeException(McpProbeResult.Fail("응답 형식 오류"));
        }

        public Task<JsonObject> ReceiveAsync(int id, CancellationToken token)
        {
            var reply = _pending ?? throw new McpProbeException(McpProbeResult.Fail("응답 없음"));
            _pending = null;
            return Task.FromResult(reply);
        }

        /// <summary>Ends the server-side session (best effort, short timeout).</summary>
        public async Task CloseAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
                AddHeaders(request, headers);
                using var _ = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
            }
        }
    }

    /// <summary>Legacy HTTP+SSE: a GET event stream announces the POST endpoint; replies arrive on the stream.</summary>
    private sealed class SseSession : IMcpSession, IDisposable
    {
        private readonly HttpClient _client;
        private readonly IReadOnlyDictionary<string, string> _headers;
        private readonly HttpResponseMessage _response;
        private readonly IAsyncEnumerator<SseEvent> _events;
        private Uri? _endpoint;

        private SseSession(HttpClient client, IReadOnlyDictionary<string, string> headers, HttpResponseMessage response, IAsyncEnumerator<SseEvent> events)
        {
            _client = client;
            _headers = headers;
            _response = response;
            _events = events;
        }

        public static async Task<SseSession> OpenAsync(HttpClient client, Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            AddHeaders(request, headers);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                ThrowForStatus(response);
                var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var session = new SseSession(client, headers, response, ReadEvents(stream, token).GetAsyncEnumerator(token));
                while (session._endpoint is null)
                {
                    if (!await session._events.MoveNextAsync().ConfigureAwait(false))
                    {
                        throw new McpProbeException(McpProbeResult.Fail("SSE 엔드포인트 없음"));
                    }

                    if (session._events.Current.Event == "endpoint")
                    {
                        // The configured headers go to the endpoint too: only the configured origin may receive them.
                        var endpoint = new Uri(uri, session._events.Current.Data.Trim());
                        if (!string.Equals(endpoint.GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                        {
                            throw new McpProbeException(McpProbeResult.Fail("다른 출처의 엔드포인트라 측정하지 않음"));
                        }

                        session._endpoint = endpoint;
                    }
                }

                return session;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        public async Task SendAsync(JsonObject message, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            AddHeaders(request, _headers);
            using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
            ThrowForStatus(response);
        }

        public async Task<JsonObject> ReceiveAsync(int id, CancellationToken token)
        {
            while (await _events.MoveNextAsync().ConfigureAwait(false))
            {
                if (Parse(_events.Current.Data) is not { } message)
                {
                    continue;
                }

                if (await AnswerServerRequestAsync(message, this, token).ConfigureAwait(false))
                {
                    continue;
                }

                if (message["id"] is JsonValue value && value.TryGetValue<int>(out var got) && got == id)
                {
                    return message;
                }
            }

            throw new McpProbeException(McpProbeResult.Fail("서버가 응답 없이 연결을 닫음"));
        }

        public void Dispose()
        {
            _ = _events.DisposeAsync().AsTask();
            _response.Dispose();
        }
    }

    private readonly record struct SseEvent(string Event, string Data);

    private static async IAsyncEnumerable<SseEvent> ReadEvents(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var name = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(name, data.ToString());
                }

                name = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart(' '));
                if (data.Length > MaxLineChars)
                {
                    throw new McpProbeException(McpProbeResult.Fail("응답이 너무 큼"));
                }
            }
        }

        if (data.Length > 0)
        {
            yield return new SseEvent(name, data.ToString());
        }
    }

    // ---------------------------------------------------------------- protocol

    private interface IMcpSession
    {
        Task SendAsync(JsonObject message, CancellationToken token);

        Task<JsonObject> ReceiveAsync(int id, CancellationToken token);
    }

    private static async Task<McpProbeResult> RunAsync(IMcpSession session, CancellationToken token)
    {
        await session.SendAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "dotdeck-context-probe", ["version"] = "1.0" },
        }), token).ConfigureAwait(false);
        CheckError(await session.ReceiveAsync(1, token).ConfigureAwait(false));
        await session.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, token).ConfigureAwait(false);

        var tools = new List<JsonNode>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var id = 2 + page;
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            await session.SendAsync(Request(id, "tools/list", parameters), token).ConfigureAwait(false);
            var reply = CheckError(await session.ReceiveAsync(id, token).ConfigureAwait(false));
            if (reply["result"]?["tools"] is JsonArray list)
            {
                tools.AddRange(list.Where(t => t is not null)!);
            }

            cursor = reply["result"]?["nextCursor"] is JsonValue next && next.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
            if (cursor is null)
            {
                break;
            }
        }

        return new McpProbeResult(ContextItemState.Measured, tools.Count, EstimateTools(tools), null);
    }

    /// <summary>The model sees each tool's name, description and input schema.</summary>
    internal static int EstimateTools(IEnumerable<JsonNode> tools) => tools.Sum(tool =>
    {
        var shown = new JsonObject
        {
            ["name"] = tool["name"]?.DeepClone(),
            ["description"] = tool["description"]?.DeepClone(),
            ["input_schema"] = tool["inputSchema"]?.DeepClone(),
        };
        return TokenEstimator.Estimate(shown.ToJsonString());
    });

    private static JsonObject Request(int id, string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    private static JsonObject CheckError(JsonObject reply)
    {
        if (reply["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue value && value.TryGetValue<int>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : "?";
            throw new McpProbeException(McpProbeResult.Fail("서버 오류 (" + code + ")"));
        }

        return reply;
    }

    /// <summary>Answers requests the server sends to the client (ping, roots/list); true when the message was one.</summary>
    private static async Task<bool> AnswerServerRequestAsync(JsonObject message, IMcpSession session, CancellationToken token)
    {
        if (message["method"] is not JsonValue method || message["id"] is not { } id)
        {
            return message["method"] is not null; // a notification
        }

        var name = method.TryGetValue<string>(out var text) ? text : string.Empty;
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (name == "ping")
        {
            reply["result"] = new JsonObject();
        }
        else if (name == "roots/list")
        {
            reply["result"] = new JsonObject { ["roots"] = new JsonArray() };
        }
        else
        {
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
        }

        await session.SendAsync(reply, token).ConfigureAwait(false);
        return true;
    }

    private static JsonObject? Parse(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class McpProbeException(McpProbeResult result) : Exception(result.Reason)
    {
        public McpProbeResult Result { get; } = result;
    }
}

/// <summary>How a stdio server's command is started on Windows: PATH/PATHEXT lookup, .cmd/.bat through cmd.exe.</summary>
public static class McpCommandResolver
{
    private static readonly string[] s_defaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public sealed record Launch(string? Executable, string? CommandLine, string? Error);

    public static Launch Resolve(McpServerSpec server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            return new Launch(null, null, "실행 명령 없음");
        }

        var path = server.Environment.TryGetValue("PATH", out var configuredPath) ? configuredPath : System.Environment.GetEnvironmentVariable("PATH");
        var program = Find(server.Command, path);
        if (program is null)
        {
            return new Launch(null, null, "실행 파일을 찾지 못함");
        }

        var extension = Path.GetExtension(program);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe re-parses the line: characters it treats specially are refused rather than escaped.
            if (server.Arguments.Any(a => a.IndexOfAny(['&', '|', '<', '>', '^', '%', '"', '!', '\n', '\r']) >= 0))
            {
                return new Launch(null, null, "배치 파일 인수에 cmd 특수 문자가 있어 실행하지 않음");
            }

            var cmd = System.Environment.GetEnvironmentVariable("ComSpec") is { } comspec && Path.IsPathFullyQualified(comspec)
                ? comspec
                : Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
            var inner = string.Join(' ', [Quote(program), .. server.Arguments.Select(Quote)]);
            return new Launch(cmd, $"{Quote(cmd)} /d /s /c \"{inner}\"", null);
        }

        return new Launch(program, WindowsCommandLine.Join(program, server.Arguments), null);
    }

    private static string Quote(string value) => value.Length > 0 && value.IndexOfAny([' ', '\t']) < 0 ? value : "\"" + value + "\"";

    private static string? Find(string command, string? path)
    {
        var hasExtension = Path.HasExtension(command);
        var extensions = (System.Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.StartsWith('.'))
            .ToArray();
        if (extensions.Length == 0)
        {
            extensions = s_defaultExtensions;
        }

        IEnumerable<string> Candidates(string basePath) => hasExtension ? [basePath] : extensions.Select(e => basePath + e);

        if (Path.IsPathFullyQualified(command))
        {
            return Candidates(command).FirstOrDefault(File.Exists);
        }

        if (command.Contains('/', StringComparison.Ordinal) || command.Contains('\\', StringComparison.Ordinal))
        {
            return null; // relative paths depend on the CLI's own working folder: not guessed
        }

        foreach (var dir in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = dir.Trim('"');
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            if (Candidates(Path.Combine(folder, command)).FirstOrDefault(File.Exists) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
