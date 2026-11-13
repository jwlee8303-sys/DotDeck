using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Mcp;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.Explorer.Visibility;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class McpProtocolTests
{
    private sealed class Host : IAiSessionHost
    {
        public int Calls;
        public bool Fail;
        public bool Large;
        public Task<JsonNode?> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) throw new IOException(@"C:\outside\private.pem: content");
            return Task.FromResult<JsonNode?>(new JsonObject { ["selection"] = Large ? new string('가', 100000) : "API_KEY=pretend-key\nhello" });
        }
    }

    private static Task<string?> Initialize(McpServer server) => server.HandleAsync("""{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-03-26"}}""", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Initialize_echoes_supported_version_and_lists_five_typed_tools()
    {
        var server = new McpServer(new Host());
        var init = JsonNode.Parse((await Initialize(server))!)!;
        Assert.Equal("init", init["id"]!.GetValue<string>());
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        var list = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", TestContext.Current.CancellationToken))!)!;
        Assert.Equal(5, list["result"]!["tools"]!.AsArray().Count);
        Assert.All(list["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        Assert.All(list["result"]!["tools"]!.AsArray(), t =>
        {
            Assert.Equal(t!["name"]!.GetValue<string>() != "open_in_editor", t["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            Assert.False(t["annotations"]!["destructiveHint"]!.GetValue<bool>());
        });
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("{\"id\":1,\"method\":\"ping\"}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"ping\"}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", -32601)]
    public async Task Invalid_requests_return_protocol_errors(string input, int code)
    {
        var response = JsonNode.Parse((await new McpServer(new Host()).HandleAsync(input, TestContext.Current.CancellationToken))!)!;
        Assert.Equal(code, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Notifications_do_not_execute_tools_and_results_are_masked()
    {
        var host = new Host();
        var server = new McpServer(host);
        await Initialize(server);
        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"get_active_document"}}""", TestContext.Current.CancellationToken));
        Assert.Equal(0, host.Calls);
        var response = await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_active_document"}}""", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("pretend-key", response!, StringComparison.Ordinal);
        host.Fail = true;
        response = await server.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_projects"}}""", TestContext.Current.CancellationToken);
        Assert.True(JsonNode.Parse(response!)!["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain("private.pem", response!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_results_are_reported_as_errors_within_the_framing_limit()
    {
        var server = new McpServer(new Host { Large = true });
        await Initialize(server);
        var response = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_active_document"}}""", TestContext.Current.CancellationToken);
        Assert.True(response!.Length <= JsonRpcFraming.MaxMessageLength);
        Assert.True(JsonNode.Parse(response)!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Framing_handles_crlf_unicode_eof_and_rejects_oversized_messages()
    {
        using var input = new StringReader("한글\r\n{}\nlast");
        Assert.Equal("한글", await JsonRpcFraming.ReadAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal("{}", await JsonRpcFraming.ReadAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal("last", await JsonRpcFraming.ReadAsync(input, TestContext.Current.CancellationToken));
        Assert.Null(await JsonRpcFraming.ReadAsync(input, TestContext.Current.CancellationToken));
        using var big = new StringReader(new string('x', JsonRpcFraming.MaxMessageLength + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => JsonRpcFraming.ReadAsync(big, TestContext.Current.CancellationToken));
    }
}

public sealed class SessionToolsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly string _other;
    private readonly List<ProjectDefinition> _projects;
    private SessionScope? _scope;
    private readonly SessionTools _tools;
    private readonly List<string> _opened = [];
    private readonly Resolver _resolver = new();

    public SessionToolsTests()
    {
        _root = _temp.Combine("one");
        _other = _temp.Combine("two");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_other);
        _projects =
        [
            new() { Id = "one", Name = "프로젝트", Kind = ProjectKind.Folder, Root = _root, Visibility = new VisibilitySettings { DefaultExtensions = ["md"] } },
            new() { Id = "two", Name = "프로젝트", Kind = ProjectKind.Folder, Root = _other },
        ];
        var paths = new AppPaths(_temp.Combine("home"), HomeSource.CommandLine, _temp.Combine("r"), _temp.Combine("l"));
        _scope = SessionScope.Create("one", ["two"], _projects, paths, PhysicalFileStore.Instance).Scope!;
        _tools = new SessionTools(() => _scope, () => _projects, PhysicalFileStore.Instance, _resolver,
            _ => Task.FromResult(new EditorContext([
                new(Path.Combine(_root, "readme.md")), new(Path.Combine(_root, "auth.json")), new(_temp.Combine("outside.md"))
            ], new(Path.Combine(_root, "readme.md"), "selected", 3, 8))),
            (path, _) => { _opened.Add(path); return Task.CompletedTask; });
    }

    public void Dispose() => _temp.Dispose();
    private Task<JsonNode?> Call(string name, JsonObject? args = null) => _tools.CallAsync(name, args ?? new(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Open_documents_and_selection_exclude_secrets_and_outside_paths()
    {
        var open = (await Call("get_open_documents"))!.AsArray();
        Assert.Single(open);
        var active = await Call("get_active_document");
        Assert.Equal("one", active!["project"]!.GetValue<string>());
        Assert.Equal("selected", active["selection"]!.GetValue<string>());
        Assert.Equal(3, active["selectionStart"]!.GetValue<int>());
    }

    [Fact]
    public async Task Search_includes_formerly_excluded_folders_and_checks_visibility_and_final_targets()
    {
        foreach (var name in new[] { "readme.md", "hidden.txt", "auth.json", "link.md", "escape.md", "hide.md" })
            File.WriteAllText(Path.Combine(_root, name), "fake file");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "excluded.md"), "fake");
        _resolver.Map[Path.Combine(_root, "link.md")] = Path.Combine(_root, "auth.json");
        _resolver.Map[Path.Combine(_root, "escape.md")] = _temp.Combine("outside.md");
        _projects[0] = _projects[0] with { Visibility = _projects[0].Visibility with { Rules = new Dictionary<string, VisibilityRule> { ["hide.md"] = VisibilityRule.Exclude } } };
        var result = await Call("find_files", new() { ["project"] = "one", ["pattern"] = "*.md" });
        var paths = result!["files"]!.AsArray();
        Assert.Equal(new[] { Path.Combine(_root, "node_modules", "excluded.md"), Path.Combine(_root, "readme.md") },
            paths.Select(p => p!.GetValue<string>()).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.False(result["truncated"]!.GetValue<bool>());
        await Assert.ThrowsAsync<ArgumentException>(() => Call("find_files", new() { ["project"] = "프로젝트", ["pattern"] = "*.md" }));
    }

    [Fact]
    public async Task Scope_narrowing_and_stop_apply_to_every_tool()
    {
        var path = Path.Combine(_other, "two.md");
        File.WriteAllText(path, "fake");
        await Call("open_in_editor", new() { ["path"] = path });
        _scope = _scope!.Without("two");
        Assert.Single((await Call("list_projects"))!.AsArray());
        await Assert.ThrowsAsync<ArgumentException>(() => Call("open_in_editor", new() { ["path"] = path }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call("find_files", new() { ["project"] = "two", ["pattern"] = "*" }));
        Assert.Single(_opened);
        _scope = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call("get_active_document"));
    }

    [Theory]
    [InlineData("../one/readme.md")]
    [InlineData("auth.json")]
    public async Task Open_rejects_relative_or_secret_paths(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Call("open_in_editor", new() { ["path"] = path }));
        Assert.Empty(_opened);
    }

    private sealed class Resolver : IFinalPathResolver
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PathExistence Probe(string fullPath) => PathExistence.Exists;
        public string? ResolveFinalPath(string fullPath) => Map.GetValueOrDefault(fullPath, fullPath);
    }
}
