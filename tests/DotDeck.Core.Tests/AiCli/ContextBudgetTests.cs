using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class ContextBudgetTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Home => _temp.Combine("home");

    private string Project => _temp.Combine("work", "repo", "app");

    private string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void MakeRepo()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(_temp.Combine("work", "repo", ".git"));
    }

    // ---------------------------------------------------------------- estimator

    [Fact]
    public void Estimator_weighs_ascii_hangul_and_collapses_whitespace()
    {
        Assert.Equal(0, TokenEstimator.Estimate(null));
        Assert.Equal(2, TokenEstimator.Estimate("abcdefgh"));
        Assert.Equal(4, TokenEstimator.Estimate("가나다라"));
        Assert.Equal(TokenEstimator.Estimate("a b"), TokenEstimator.Estimate("a    \n\n  b"));
        Assert.Equal("1.8만", TokenEstimator.Format(18_000));
        Assert.Equal("4,200", TokenEstimator.Format(4200));
    }

    // ---------------------------------------------------------------- collector

    [Fact]
    public void Claude_collects_global_project_imports_rules_skills_and_agents()
    {
        MakeRepo();
        Write(Path.Combine(Home, ".claude", "CLAUDE.md"), "global rules\n@~/notes/extra.md\n```\n@not-an-import.md\n```\n`@inline.md`");
        Write(Path.Combine(Home, "notes", "extra.md"), "imported text");
        Write(Path.Combine(Home, "not-an-import.md"), "never");
        Write(Path.Combine(Project, "CLAUDE.md"), "project rules @./more.md");
        Write(Path.Combine(Project, "more.md"), "more");
        Write(Path.Combine(Project, ".claude", "rules", "always.md"), "always loaded");
        Write(Path.Combine(Project, ".claude", "rules", "scoped.md"), "---\npaths: src/**\n---\nonly for src");
        Write(Path.Combine(Home, ".claude", "skills", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: >\n  Deploys the app\n  to production\n---\nbody that is not loaded");
        Write(Path.Combine(Home, ".claude", "skills", "manual", "SKILL.md"), "---\nname: manual\ndescription: x\ndisable-model-invocation: true\n---\n");
        Write(Path.Combine(Project, ".claude", "agents", "reviewer.md"), "---\nname: reviewer\ndescription: Reviews code\n---\nprompt");

        var result = new ContextCollector(Home).Collect(CliKind.Claude, Project);

        var names = result.Items.Select(i => (i.Category, i.Name)).ToList();
        Assert.Contains((ContextCategory.Instructions, ".claude/CLAUDE.md"), names);
        Assert.Contains((ContextCategory.Instructions, "notes/extra.md"), names);
        Assert.Contains((ContextCategory.Instructions, "app/CLAUDE.md"), names);
        Assert.Contains((ContextCategory.Instructions, "app/more.md"), names);
        Assert.Contains((ContextCategory.Instructions, "rules/always.md"), names);
        Assert.DoesNotContain(names, n => n.Name.Contains("scoped", StringComparison.Ordinal) || n.Name.Contains("not-an-import", StringComparison.Ordinal));
        var deploy = Assert.Single(result.Items, i => i.Name == "deploy");
        Assert.Equal(TokenEstimator.Estimate("deploy: Deploys the app to production"), deploy.Tokens);
        Assert.DoesNotContain(result.Items, i => i.Name == "manual");
        Assert.Contains(result.Items, i => i.Category == ContextCategory.Agents && i.Name == "reviewer");
        Assert.Contains(result.WatchDirectories, w => w.Directory == Path.Combine(Project, ".claude") && w.Recursive);
    }

    [Fact]
    public void Claude_mcp_servers_come_from_user_local_and_project_scope_with_disabled_marked()
    {
        MakeRepo();
        var projectKey = Project.Replace('\\', '/');
        Write(Path.Combine(Home, ".claude.json"), $$"""
            {
              "mcpServers": {
                "notion": { "type": "http", "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer SECRET-TOKEN" } },
                "off": { "command": "node", "args": ["x.js"], "env": { "API_KEY": "SECRET-KEY" } }
              },
              "projects": { "{{projectKey}}": { "mcpServers": { "local": { "command": "uvx", "args": ["tool"] } }, "disabledMcpServers": ["off"] } }
            }
            """);
        Write(Path.Combine(Project, ".mcp.json"), """{ "mcpServers": { "shared": { "url": "https://example.test/sse", "type": "sse" } } }""");

        var result = new ContextCollector(Home).Collect(CliKind.Claude, Project);

        var servers = result.McpServers.ToDictionary(s => s.Name);
        Assert.Equal(McpTransport.Http, servers["notion"].Transport);
        Assert.Equal("Bearer SECRET-TOKEN", servers["notion"].Headers["Authorization"]);
        Assert.True(servers["off"].Disabled);
        Assert.False(servers["local"].Disabled);
        Assert.Equal(McpTransport.Sse, servers["shared"].Transport);
        Assert.DoesNotContain("SECRET", servers["off"].ToString(), StringComparison.Ordinal);

        var report = ContextAnalyzer.Build(CliKind.Claude, Project, result, null, null, new ContextBudgetSettings(), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(report.Items, i => (i.Detail ?? string.Empty).Contains("SECRET", StringComparison.Ordinal));
        Assert.Contains(report.Items, i => i.Name == "off MCP" && i.State == ContextItemState.Disabled);
        Assert.Contains(report.Items, i => i.Name == "notion MCP" && (i.Detail ?? string.Empty).Contains("기타에 포함", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_reads_override_first_and_the_chain_from_the_git_root_and_mcp_tables()
    {
        MakeRepo();
        var codex = _temp.Combine("codexhome");
        Write(Path.Combine(codex, "AGENTS.md"), "plain");
        Write(Path.Combine(codex, "AGENTS.override.md"), "override wins");
        Write(_temp.Combine("work", "AGENTS.md"), "outside the repo");
        Write(_temp.Combine("work", "repo", "AGENTS.md"), "repo root");
        Write(Path.Combine(Project, "AGENTS.md"), "app folder");
        Write(Path.Combine(codex, "skills", ".system", "helper", "SKILL.md"), "---\nname: helper\ndescription: helps\n---\n");
        Write(Path.Combine(codex, "config.toml"), """
            model = "x"
            [mcp_servers.docs]
            command = "npx"
            args = ["-y", "docs-server"]
            env = { TOKEN = "SECRET" }

            [mcp_servers.remote]
            url = "https://example.test/mcp"
            enabled = false
            """);

        var result = new ContextCollector(Home, codex).Collect(CliKind.Codex, Project);

        var paths = result.Items.Where(i => i.Category == ContextCategory.Instructions).Select(i => i.Path).ToList();
        Assert.Contains(Path.Combine(codex, "AGENTS.override.md"), paths);
        Assert.DoesNotContain(Path.Combine(codex, "AGENTS.md"), paths);
        Assert.Contains(_temp.Combine("work", "repo", "AGENTS.md"), paths);
        Assert.Contains(Path.Combine(Project, "AGENTS.md"), paths);
        Assert.DoesNotContain(_temp.Combine("work", "AGENTS.md"), paths);
        Assert.Contains(result.Items, i => i.Category == ContextCategory.Skills && i.Name == "helper");
        var docs = Assert.Single(result.McpServers, s => s.Name == "docs");
        Assert.Equal(["-y", "docs-server"], docs.Arguments);
        Assert.Equal("SECRET", docs.Environment["TOKEN"]);
        Assert.True(Assert.Single(result.McpServers, s => s.Name == "remote").Disabled);
    }

    [Fact]
    public void Agy_counts_always_on_rules_and_only_the_description_of_model_decision_rules()
    {
        MakeRepo();
        Write(Path.Combine(Project, "GEMINI.md"), "gemini rules");
        Write(Path.Combine(Project, ".agents", "rules", "style.md"), "always");
        Write(Path.Combine(Project, ".agents", "rules", "maybe.md"), "---\ntrigger: model_decision\ndescription: When writing SQL\n---\nlong body " + new string('x', 400));
        Write(Path.Combine(Project, ".agents", "rules", "glob.md"), "---\ntrigger: glob\n---\nbody");
        Write(Path.Combine(Home, ".gemini", "config", "mcp_config.json"), """{ "mcpServers": { "db": { "serverUrl": "https://example.test/mcp" } } }""");

        var result = new ContextCollector(Home).Collect(CliKind.Antigravity, Project);

        Assert.Contains(result.Items, i => i.Name == "app/GEMINI.md");
        Assert.Contains(result.Items, i => i.Name == "rules/style.md");
        Assert.Equal(TokenEstimator.Estimate("When writing SQL"), Assert.Single(result.Items, i => i.Name == "rules/maybe.md").Tokens);
        Assert.DoesNotContain(result.Items, i => i.Name == "rules/glob.md");
        Assert.Equal("https://example.test/mcp", Assert.Single(result.McpServers).Url);
    }

    [Fact]
    public void Unreadable_configuration_is_a_problem_and_the_rest_continues()
    {
        MakeRepo();
        Write(Path.Combine(Home, ".claude.json"), "{ not json");
        Write(Path.Combine(Project, "CLAUDE.md"), "still read");

        var result = new ContextCollector(Home).Collect(CliKind.Claude, Project);

        Assert.Contains(result.Problems, p => p.Contains("JSON", StringComparison.Ordinal));
        Assert.Contains(result.Items, i => i.Name == "app/CLAUDE.md");
    }

    // ---------------------------------------------------------------- analyzer

    [Fact]
    public void Suggestions_follow_the_thresholds_and_the_log_baseline_splits_off_the_rest()
    {
        ContextItem[] items =
        [
            new(ContextCategory.Instructions, "CLAUDE.md", @"C:\p\CLAUDE.md", 4200),
            new(ContextCategory.Skills, "long", @"C:\s\SKILL.md", 300),
            new(ContextCategory.Skills, "short", @"C:\t\SKILL.md", 20),
        ];
        var collection = new ContextCollection(items, [], [], []);
        var log = new SessionLogMeasurement(SessionLogState.Measured, "x.jsonl", Baseline: 18_000, CurrentInput: 30_000, ContextWindow: 200_000);
        var settings = new ContextBudgetSettings { TotalWarningTokens = 10_000 };

        var report = ContextAnalyzer.Build(CliKind.Claude, null, collection, null, null, settings, DateTimeOffset.UtcNow, log);

        Assert.Equal(4520, report.Total);
        Assert.Equal(18_000, report.Baseline);
        Assert.Equal(18_000 - 4520, report.Other);
        Assert.Equal("CLAUDE.md", report.Items[0].Name);
        Assert.Contains(report.Suggestions, s => s.Text.StartsWith("기본 소모 약 1.8만", StringComparison.Ordinal));
        Assert.Contains(report.Suggestions, s => s.Text.Contains("4,200 토큰 → 1,500 이하 권장", StringComparison.Ordinal) && s.Path == @"C:\p\CLAUDE.md");
        Assert.Contains(report.Suggestions, s => s.Text.Contains("long 300", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Suggestions, s => s.Text.Contains("short", StringComparison.Ordinal));
    }

    [Fact]
    public void Direct_measurement_shows_stored_results_until_the_configuration_changes()
    {
        var store = new McpMeasurementStore(new PhysicalFileStore(), _temp.Combine("state", "context-mcp.json"));
        var server = new McpServerSpec { Name = "notion", Source = "사용자", SourcePath = @"C:\h\.claude.json", Command = "node", Environment = new Dictionary<string, string> { ["KEY"] = "SECRET-VALUE" } };
        store.Save(new McpMeasurement(server.Key, server.Name, 12, 9000, DateTimeOffset.UtcNow, server.ConfigHash));
        var settings = new ContextBudgetSettings { McpDirectMeasure = true };
        var collection = new ContextCollection([new(ContextCategory.Instructions, "a.md", null, 9000)], [server], [], []);

        var report = ContextAnalyzer.Build(CliKind.Claude, null, collection, store, null, settings, DateTimeOffset.UtcNow);
        Assert.Equal(9000, Assert.Single(report.Items, i => i.Category == ContextCategory.Mcp).Tokens);
        Assert.Contains(report.Suggestions, s => s.Text.StartsWith("notion MCP가 9,000 토큰(50%)", StringComparison.Ordinal));

        var changed = new McpServerSpec { Name = "notion", Source = "사용자", SourcePath = @"C:\h\.claude.json", Command = "node", Environment = new Dictionary<string, string> { ["KEY"] = "OTHER" } };
        var reloaded = new McpMeasurementStore(new PhysicalFileStore(), _temp.Combine("state", "context-mcp.json"));
        Assert.Null(reloaded.Find(changed));
        Assert.True(reloaded.IsStale(changed));
        var item = ContextAnalyzer.McpItem(changed, reloaded, null);
        Assert.Equal(ContextItemState.NotMeasured, item.State);
        Assert.Contains("설정 변경됨", item.Detail, StringComparison.Ordinal);

        var stored = File.ReadAllText(_temp.Combine("state", "context-mcp.json"));
        Assert.DoesNotContain("SECRET", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("node", stored, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- session logs

    private static string Line(JsonObject node) => node.ToJsonString();

    private string CodexLog(string codexHome, DateTimeOffset started, string cwd, string threadId, bool answered = true)
    {
        var dir = Path.Combine(codexHome, "sessions", started.ToString("yyyy"), started.ToString("MM"), started.ToString("dd"));
        var lines = new List<string>
        {
            Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["cwd"] = cwd, ["timestamp"] = started.ToString("O"), ["id"] = threadId } }),
            Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "developer", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "system" }) } }),
            Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "# AGENTS.md instructions " + new string('a', 4000) }) } }),
            Line(new JsonObject { ["type"] = "turn_context", ["payload"] = new JsonObject() }),
            Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "fix the bug" }) } }),
            Line(new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "token_count", ["info"] = null } }),
        };
        if (answered)
        {
            lines.Add(Line(TokenCount(20_000, 475_000)));
            lines.Add(Line(TokenCount(60_000, 475_000)));
        }

        lines.Add("not json at all");
        return Write(Path.Combine(dir, $"rollout-{started:yyyy-MM-ddTHH-mm-ss}-{threadId}.jsonl"), string.Join("\n", lines) + "\n");
    }

    private static JsonObject TokenCount(int input, int window) => new()
    {
        ["type"] = "event_msg",
        ["payload"] = new JsonObject
        {
            ["type"] = "token_count",
            ["info"] = new JsonObject
            {
                ["last_token_usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = 10 },
                ["model_context_window"] = window,
            },
        },
    };

    [Fact]
    public void Codex_log_baseline_skips_the_agents_injection_and_uses_the_prompt_after_turn_context()
    {
        var codex = _temp.Combine("codex");
        var started = DateTimeOffset.Now;
        CodexLog(codex, started, @"D:\work\app", "01aa-thread");

        var result = new SessionLogReader(_temp.Combine("claude"), codex).Read(new SessionLogQuery(CliKind.Codex, @"D:\work\app\", started), []);

        Assert.Equal(SessionLogState.Measured, result.State);
        Assert.Equal(TokenEstimator.Estimate("fix the bug"), result.FirstPromptTokens);
        Assert.Equal(20_000 - result.FirstPromptTokens, result.Baseline);
        Assert.Equal(60_000, result.CurrentInput);
        Assert.Equal(475_000, result.ContextWindow);
        Assert.False(result.WindowIsEstimated);
    }

    [Fact]
    public void Codex_log_waits_for_the_first_answer_and_two_candidates_are_not_matched()
    {
        var codex = _temp.Combine("codex");
        var started = DateTimeOffset.Now;
        CodexLog(codex, started, @"D:\work\app", "one", answered: false);
        var reader = new SessionLogReader(_temp.Combine("claude"), codex);

        Assert.Equal(SessionLogState.WaitingFirstAnswer, reader.Read(new SessionLogQuery(CliKind.Codex, @"D:\work\app", started), []).State);

        CodexLog(codex, started.AddSeconds(5), @"D:\work\app", "two");
        Assert.Equal(SessionLogState.NotMatched, reader.Read(new SessionLogQuery(CliKind.Codex, @"D:\work\app", started), []).State);
        Assert.Equal(SessionLogState.Measured, reader.Read(new SessionLogQuery(CliKind.Codex, @"D:\work\app", started, "two"), []).State);
        Assert.Equal(SessionLogState.Unsupported, reader.Read(new SessionLogQuery(CliKind.Antigravity, @"D:\work\app", started), []).State);
    }

    [Fact]
    public void Claude_log_dedupes_responses_skips_sidechains_and_estimates_the_window_from_the_model_table()
    {
        var claude = _temp.Combine("claude");
        var id = Guid.NewGuid().ToString();
        var started = DateTimeOffset.UtcNow;
        static JsonObject Assistant(string messageId, int input, int creation, int read, bool sidechain = false) => new()
        {
            ["type"] = "assistant",
            ["isSidechain"] = sidechain,
            ["message"] = new JsonObject
            {
                ["id"] = messageId,
                ["model"] = "claude-opus-5-5",
                ["usage"] = new JsonObject { ["input_tokens"] = input, ["cache_creation_input_tokens"] = creation, ["cache_read_input_tokens"] = read, ["output_tokens"] = 5 },
            },
        };
        string[] lines =
        [
            Line(new JsonObject { ["type"] = "mode", ["timestamp"] = started.ToString("O") }),
            Line(new JsonObject { ["type"] = "user", ["isMeta"] = true, ["message"] = new JsonObject { ["content"] = "meta caveat" } }),
            Line(new JsonObject { ["type"] = "user", ["cwd"] = @"D:\w", ["message"] = new JsonObject { ["content"] = "hello there" } }),
            Line(Assistant("m1", 2, 19_000, 25_000)),
            Line(Assistant("m1", 2, 19_000, 25_000)),
            Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["content"] = "x" }) } }),
            Line(Assistant("s1", 1, 900_000, 0, sidechain: true)),
            Line(Assistant("m2", 2, 600, 44_000)),
        ];
        Write(Path.Combine(claude, "projects", "D--w", id + ".jsonl"), string.Join("\n", lines));
        var reader = new SessionLogReader(claude, _temp.Combine("codex"));
        ModelWindowRule[] table = [new("claude-", 200_000), new("claude-opus-5", 1_000_000)];

        var result = reader.Read(new SessionLogQuery(CliKind.Claude, @"D:\w", started, id), table);

        Assert.Equal(SessionLogState.Measured, result.State);
        Assert.Equal(44_002 - TokenEstimator.Estimate("hello there"), result.Baseline);
        Assert.Equal(44_602, result.CurrentInput);
        Assert.Equal(1_000_000, result.ContextWindow);
        Assert.True(result.WindowIsEstimated);

        var small = reader.Read(new SessionLogQuery(CliKind.Claude, @"D:\w", started, id), [new("claude-", 10_000)]);
        Assert.Null(small.ContextWindow); // the input is already larger than the table says: tokens only

        var byFolder = reader.Read(new SessionLogQuery(CliKind.Claude, @"D:\w", started.AddSeconds(-5)), table);
        Assert.Equal(SessionLogState.Measured, byFolder.State);
        Assert.Equal("D--w", SessionLogReader.ClaudeProjectFolder(@"D:\w"));
    }

    [Fact]
    public void Claude_log_without_a_prompt_is_unreadable_not_an_exception()
    {
        var claude = _temp.Combine("claude");
        var id = Guid.NewGuid().ToString();
        Write(Path.Combine(claude, "projects", "D--w", id + ".jsonl"),
            """{"type":"assistant","message":{"id":"m","usage":{"input_tokens":5}}}""" + "\n{{{broken\n");

        var result = new SessionLogReader(claude, _temp.Combine("codex")).Read(new SessionLogQuery(CliKind.Claude, @"D:\w", DateTimeOffset.UtcNow, id), []);

        Assert.Equal(SessionLogState.Unreadable, result.State);
    }

    // ---------------------------------------------------------------- probe

    [Fact]
    public async Task Probe_lists_all_pages_answers_pings_and_ends_the_process()
    {
        var host = new FakeHost(ServerScript);
        var probe = new McpProbe(host, timeout: TimeSpan.FromSeconds(5));
        var server = new McpServerSpec { Name = "fake", Source = "t", SourcePath = "t", Command = Environment.ProcessPath! };

        var result = await probe.MeasureAsync(server, _temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(ContextItemState.Measured, result.State);
        Assert.Equal(3, result.ToolCount);
        Assert.True(result.Tokens > 0);
        Assert.True(host.Last!.Disposed);
        Assert.Contains("notifications/initialized", host.Last.Received);
    }

    [Fact]
    public async Task Probe_times_out_and_reports_failure_without_details()
    {
        var host = new FakeHost(async (_, _, _) => await Task.Delay(Timeout.Infinite).ConfigureAwait(false));
        var probe = new McpProbe(host, timeout: TimeSpan.FromMilliseconds(300));
        var server = new McpServerSpec { Name = "slow", Source = "t", SourcePath = "t", Command = Environment.ProcessPath! };

        var result = await probe.MeasureAsync(server, _temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(ContextItemState.Failed, result.State);
        Assert.Contains("응답 없음", result.Reason, StringComparison.Ordinal);
        Assert.True(host.Last!.Disposed);
    }

    [Fact]
    public async Task Probe_never_contacts_oauth_servers_and_reports_401_as_auth_required()
    {
        var handler = new StatusHandler(HttpStatusCode.Unauthorized);
        var probe = new McpProbe(null, handler);

        var oauth = await probe.MeasureAsync(new McpServerSpec { Name = "o", Source = "t", SourcePath = "t", Transport = McpTransport.Http, Url = "https://example.test/mcp", UsesOAuth = true }, null, TestContext.Current.CancellationToken);
        Assert.Equal(ContextItemState.AuthRequired, oauth.State);
        Assert.Equal(0, handler.Calls);

        var remote = await probe.MeasureAsync(new McpServerSpec { Name = "r", Source = "t", SourcePath = "t", Transport = McpTransport.Http, Url = "https://example.test/mcp" }, null, TestContext.Current.CancellationToken);
        Assert.Equal(ContextItemState.AuthRequired, remote.State);
    }

    [Fact]
    public async Task Probe_measures_a_streamable_http_server()
    {
        var handler = new JsonHandler();
        var probe = new McpProbe(null, handler);

        var result = await probe.MeasureAsync(new McpServerSpec { Name = "h", Source = "t", SourcePath = "t", Transport = McpTransport.Http, Url = "https://example.test/mcp", Headers = new Dictionary<string, string> { ["X-Key"] = "k" } }, null, TestContext.Current.CancellationToken);

        Assert.Equal(ContextItemState.Measured, result.State);
        Assert.Equal(1, result.ToolCount);
        Assert.All(handler.Keys, k => Assert.Equal("k", k));
        Assert.Equal("sess-1", handler.LastSessionId);
    }

    [Fact]
    public void Batch_launchers_go_through_cmd_and_refuse_cmd_metacharacters()
    {
        var bin = _temp.Combine("bin");
        Write(Path.Combine(bin, "tool.cmd"), "@echo off");
        var path = new Dictionary<string, string> { ["PATH"] = bin };

        var ok = McpCommandResolver.Resolve(new McpServerSpec { Name = "t", Source = "t", SourcePath = "t", Command = "tool", Arguments = ["-y", "pkg name"], Environment = path });
        Assert.Null(ok.Error);
        Assert.EndsWith("cmd.exe", ok.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/d /s /c \"", ok.CommandLine, StringComparison.Ordinal);
        Assert.Contains("\"pkg name\"", ok.CommandLine, StringComparison.Ordinal);

        var bad = McpCommandResolver.Resolve(new McpServerSpec { Name = "t", Source = "t", SourcePath = "t", Command = "tool", Arguments = ["a&b"], Environment = path });
        Assert.NotNull(bad.Error);
        Assert.NotNull(McpCommandResolver.Resolve(new McpServerSpec { Name = "t", Source = "t", SourcePath = "t", Command = "missing-tool", Environment = path }).Error);
    }

    [Fact]
    public async Task Probe_refuses_an_sse_endpoint_on_another_origin_and_sends_no_headers_there()
    {
        var handler = new SseHandler("https://other.test/messages");
        var probe = new McpProbe(null, handler);

        var result = await probe.MeasureAsync(new McpServerSpec { Name = "s", Source = "t", SourcePath = "t", Transport = McpTransport.Sse, Url = "https://example.test/sse", Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET" } }, null, TestContext.Current.CancellationToken);

        Assert.Equal(ContextItemState.Failed, result.State);
        Assert.Contains("다른 출처", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Hosts, h => h == "other.test");
    }

    [Fact]
    public async Task Probe_reports_rejected_settings_as_a_failure_instead_of_throwing()
    {
        var probe = new McpProbe(new ThrowingHost());

        var result = await probe.MeasureAsync(new McpServerSpec { Name = "x", Source = "t", SourcePath = "t", Command = Environment.ProcessPath! }, _temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(ContextItemState.Failed, result.State);
        Assert.DoesNotContain("SECRET", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("models_cache.json", false, false)]
    [InlineData("settings.json", false, true)]
    [InlineData("AGENTS.md", false, true)]
    [InlineData("config.toml", false, true)]
    [InlineData(@"skills\deploy\SKILL.md", true, true)]
    [InlineData(@"skills\deploy", true, true)]
    [InlineData("cache.json", true, true)]
    [InlineData("history.jsonl", true, false)]
    public void Watcher_ignores_noisy_files_in_plain_folders(string name, bool recursive, bool expected) =>
        Assert.Equal(expected, ContextWatcher.Matters(name, recursive));

    private sealed class ThrowingHost : IMcpProcessHost
    {
        public IMcpProcess Start(string executable, string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            throw new ArgumentException("환경 변수 이름 또는 값이 올바르지 않습니다: A=B");
    }

    private sealed class SseHandler(string endpoint) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: endpoint\ndata: {endpoint}\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    // A tiny MCP server: a ping first, then two pages of tools.
    private static async Task ServerScript(StreamReader input, StreamWriter output, List<string> received)
    {
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var method = message["method"]?.GetValue<string>();
            received.Add(method ?? "reply");
            if (method == "initialize")
            {
                await output.WriteLineAsync("server log line, not json").ConfigureAwait(false);
                await output.WriteLineAsync("""{"jsonrpc":"2.0","id":99,"method":"ping"}""").ConfigureAwait(false);
                await output.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""").ConfigureAwait(false);
            }
            else if (method == "tools/list")
            {
                var id = message["id"]!.GetValue<int>();
                var page = message["params"]?["cursor"] is null
                    ? """{"tools":[{"name":"a","description":"first","inputSchema":{"type":"object"}},{"name":"b","inputSchema":{}}],"nextCursor":"p2"}"""
                    : """{"tools":[{"name":"c","description":"third","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}}}]}""";
                await output.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{page}}}""").ConfigureAwait(false);
            }
        }
    }

    private sealed class FakeHost(Func<StreamReader, StreamWriter, List<string>, Task> server) : IMcpProcessHost
    {
        public FakeProcess? Last { get; private set; }

        public IMcpProcess Start(string executable, string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            Last = new FakeProcess(server);
            return Last;
        }
    }

    private sealed class FakeProcess : IMcpProcess
    {
        private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _serverIn;
        private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _clientIn;

        public FakeProcess(Func<StreamReader, StreamWriter, List<string>, Task> server)
        {
            _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
            _clientIn = new AnonymousPipeClientStream(PipeDirection.In, _fromServer.ClientSafePipeHandle);
            var reader = new StreamReader(_serverIn, Encoding.UTF8);
            var writer = new StreamWriter(_fromServer, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _ = Task.Run(async () =>
            {
                try
                {
                    await server(reader, writer, Received).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            });
        }

        public List<string> Received { get; } = [];

        public bool Disposed { get; private set; }

        public Stream Input => _toServer;

        public Stream Output => _clientIn;

        public void Dispose()
        {
            Disposed = true;
            _toServer.Dispose();
            _serverIn.Dispose();
            _fromServer.Dispose();
            _clientIn.Dispose();
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];

        public string? LastSessionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.TryGetValues("X-Key", out var keys) ? keys.Single() : "(none)");
            LastSessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.Single() : LastSessionId;
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            var method = body["method"]?.GetValue<string>();
            if (body["id"] is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var id = body["id"]!.GetValue<int>();
            var result = method == "initialize"
                ? """{"protocolVersion":"2025-06-18","capabilities":{}}"""
                : """{"tools":[{"name":"search","description":"Search","inputSchema":{"type":"object"}}]}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}\n\n", Encoding.UTF8, "text/event-stream"),
            };
            if (method == "initialize")
            {
                response.Headers.Add("Mcp-Session-Id", "sess-1");
            }

            return response;
        }
    }
}
