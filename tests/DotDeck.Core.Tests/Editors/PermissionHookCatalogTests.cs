using DotDeck.Core.Hooks;
using System.Text.Json;
using DotDeck.Core.Editors;
using DotDeck.Core.Files;
using DotDeck.Core.Mcp;
using DotDeck.Core.Roles;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Editors;

public sealed class PermissionRuleTests
{
    [Theory]
    [InlineData("Bash(git push -f:*)", "Bash", "git push -f:*")]
    [InlineData("Read(./**/*.pem)", "Read", "./**/*.pem")]
    [InlineData("WebSearch", "WebSearch", null)]
    [InlineData("mcp__github", "mcp__github", null)]
    [InlineData("mcp__github__create_issue", "mcp__github__create_issue", null)]
    [InlineData("Bash(echo (a) && (b))", "Bash", "echo (a) && (b)")]
    [InlineData("Bash()", "Bash", "")]
    public void Rules_split_into_tool_and_argument(string raw, string tool, string? argument)
    {
        var rule = PermissionRule.Parse(raw);

        Assert.True(rule.IsParsed);
        Assert.Equal(tool, rule.Tool);
        Assert.Equal(argument, rule.Argument);
        Assert.Equal(raw, rule.Raw);
    }

    [Theory]
    [InlineData("Bash(foo")]
    [InlineData("Read (x)")]
    [InlineData("Bash(a))x")]
    [InlineData("  ")]
    [InlineData("(x)")]
    public void Odd_rules_keep_their_text(string raw)
    {
        var rule = PermissionRule.Parse(raw);

        Assert.False(rule.IsParsed);
        Assert.Equal(raw, rule.Raw);
        Assert.Equal(raw, rule.Tool);
    }

    [Theory]
    [InlineData("Bash", "git push --force:*", "Bash(git push --force:*)")]
    [InlineData("WebSearch", "", "WebSearch")]
    [InlineData("Bash", "  ", "Bash")]
    [InlineData(" Read ", " ./src/** ", "Read(./src/**)")]
    public void Tool_and_argument_compose(string tool, string argument, string expected)
    {
        Assert.Equal(expected, PermissionRule.Compose(tool, argument));
    }

    [Fact]
    public void Lint_reports_rules_claude_code_ignores()
    {
        var tools = ClaudeToolCatalog.LoadClaude();
        Assert.Contains(PermissionRule.Lint("allow", "Write(src/**)", tools), i => i.Message.Contains("Edit(경로)", StringComparison.Ordinal));
        Assert.Contains(PermissionRule.Lint("allow", "mcp__github(x)", tools), i => i.Message.Contains("건너뜁", StringComparison.Ordinal));
        Assert.Contains(PermissionRule.Lint("allow", "mcp__*", tools), i => i.Message.Contains("건너뜁", StringComparison.Ordinal));
        Assert.Contains(PermissionRule.Lint("deny", @"Read(C:\secrets\**)", tools), i => i.Message.Contains("//c/", StringComparison.Ordinal));
        Assert.Contains(PermissionRule.Lint("allow", "Bash(foo", tools), i => i.Severity == IssueSeverity.Info);
        Assert.Contains(PermissionRule.Lint("allow", "FutureTool", tools), i => i.Message.Contains("FutureTool", StringComparison.Ordinal));
        Assert.Empty(PermissionRule.Lint("allow", "Bash(npm run test:*)", tools));
        Assert.Empty(PermissionRule.Lint("deny", "Read(//c/secrets/**)", tools));
        Assert.Empty(PermissionRule.Lint("allow", "mcp__github__create_issue", tools));
    }
}

public sealed class HookMatcherTests
{
    [Theory]
    [InlineData("", HookMatcherMode.All)]
    [InlineData("*", HookMatcherMode.All)]
    [InlineData(" ", HookMatcherMode.All)]
    [InlineData("Read|Write|Edit", HookMatcherMode.List)]
    [InlineData("Edit, Write", HookMatcherMode.List)]
    [InlineData("^(Read|mcp__github__.*)$", HookMatcherMode.List)]
    [InlineData("mcp__memory__.*", HookMatcherMode.Regex)]
    [InlineData("Notebook.*", HookMatcherMode.Regex)]
    public void Matcher_modes(string raw, HookMatcherMode mode)
    {
        Assert.Equal(mode, HookMatcher.Parse(raw).Mode);
    }

    [Fact]
    public void List_items_round_trip_to_check_states()
    {
        var matcher = HookMatcher.Parse("Read|Write|Edit");
        Assert.Equal(["Read", "Write", "Edit"], matcher.Items);
        Assert.True(matcher.Contains("Write"));

        Assert.Equal("Read|Edit", matcher.Toggle("Write", on: false));
        Assert.Equal("Read|Write|Edit|Bash", matcher.Toggle("Bash", on: true));
        Assert.Equal("Edit,Write,Bash", HookMatcher.Parse("Edit,Write").Toggle("Bash", true));
        Assert.Equal(string.Empty, HookMatcher.Parse("Read").Toggle("Read", false));
    }

    [Fact]
    public void Regex_items_make_an_anchored_list()
    {
        var text = HookMatcher.Parse("Read").Toggle("mcp__github__.*", on: true);
        Assert.Equal("^(Read|mcp__github__.*)$", text);

        var back = HookMatcher.Parse(text);
        Assert.Equal(HookMatcherMode.List, back.Mode);
        Assert.True(back.Contains("mcp__github__.*"));
        Assert.Equal("Read", back.Toggle("mcp__github__.*", false));
    }

    [Fact]
    public void Pipe_only_events_treat_commas_as_regex()
    {
        Assert.Equal(HookMatcherMode.Regex, HookMatcher.Parse(".envrc,.env", HookMatcherSyntax.PipeOnly).Mode);
        Assert.Equal(HookMatcherMode.List, HookMatcher.Parse("rate_limit,overloaded", HookMatcherSyntax.Standard).Mode);
        Assert.Equal(HookMatcherMode.Regex, HookMatcher.Parse("rate_limit,overloaded", HookMatcherSyntax.PipeOnly).Mode);
    }

    [Fact]
    public void Invalid_regex_is_only_a_warning()
    {
        Assert.NotNull(HookMatcher.Parse("Edit(").RegexError());
        Assert.Null(HookMatcher.Parse("mcp__.*").RegexError());
        Assert.Null(HookMatcher.Parse("Read|Write").RegexError());
    }
}

public sealed class ClaudeCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Built_in_catalogs_load_with_sources()
    {
        var tools = ClaudeToolCatalog.LoadClaude();
        var events = HookEventCatalog.BuiltIn;

        foreach (var name in new[] { "Bash", "Read", "Write", "Edit", "Glob", "Grep", "WebFetch", "WebSearch", "Agent", "NotebookEdit" })
        {
            Assert.NotNull(tools.Find(name));
            Assert.False(string.IsNullOrWhiteSpace(tools.Find(name)!.Description));
        }

        Assert.All(tools.Tools, t => Assert.True(tools.ArgumentKinds.ContainsKey(t.Argument), t.Name));
        Assert.True(events.Events.Count >= 30);
        Assert.Equal(events.Events.Count, events.Events.Select(e => e.Name).Distinct().Count());
        Assert.False(events.Find("Stop")!.UsesMatcher);
        Assert.True(events.Find("PreToolUse")!.MatchesTools);
        Assert.Contains("startup", events.Find("SessionStart")!.Values!);
        Assert.NotEmpty(tools.Sources);
        Assert.NotEmpty(events.Sources);
        Assert.NotEmpty(SettingsKeyCatalog.LoadClaude().Sources);
    }

    [Fact]
    public void Help_mentions_shared_rules_and_the_scope_base()
    {
        var tools = ClaudeToolCatalog.LoadClaude();
        var project = new FileRole("claude", "settings", "설정", ConfigScope.Project, @"D:\p\.claude", @"D:\p", EditorKind.ClaudeSettings, "settings", []);
        var global = project with { Scope = ConfigScope.Global, BaseRoot = @"C:\home" };

        var read = tools.PermissionHelp("Read", project);
        Assert.Contains(read, l => l.Contains(@"D:\p\path", StringComparison.Ordinal));
        Assert.Contains(read, l => l.Contains("Grep", StringComparison.Ordinal));
        Assert.Contains(tools.PermissionHelp("Read", global), l => l.Contains("~/.claude/path", StringComparison.Ordinal));
        Assert.Contains(tools.PermissionHelp("Edit", project), l => l.Contains("Write", StringComparison.Ordinal));
        Assert.Contains(tools.PermissionHelp("mcp__github", project), l => l.Contains("mcp__서버", StringComparison.Ordinal));
        Assert.Contains(tools.PermissionHelp("Unknown", project), l => l.Contains("목록에 없는", StringComparison.Ordinal));
        Assert.Equal(4, HookEventCatalog.BuiltIn.Find("PreToolUse")!.HelpLines().Count);
    }

    [Fact]
    public void Broken_tool_catalog_override_falls_back()
    {
        var dir = _temp.Combine("catalogs");
        Directory.CreateDirectory(Path.Combine(dir, "Claude"));
        File.WriteAllText(Path.Combine(dir, "Claude", "tools.json"), "{\"schemaVersion\":1,\"tools\":[null]}");

        var loaded = DotDeck.Core.Catalogs.CatalogResources.Load(ClaudeToolCatalog.ClaudeResource, ClaudeToolCatalog.Parse, PhysicalFileStore.Instance, dir);

        Assert.Equal(DotDeck.Core.Catalogs.CatalogOrigin.BuiltIn, loaded.Origin);
        Assert.Throws<InvalidDataException>(() => HookEventCatalog.Parse("{\"schemaVersion\":1,\"events\":null}"));
    }

    [Fact]
    public void Unknown_events_and_tools_are_kept_through_the_form_round_trip()
    {
        const string json = """
            {
              "model": "opus",
              "permissions": { "allow": ["FutureTool(x)", "Bash(foo"], "deny": ["Read(./.env)"], "defaultMode": "plan" },
              "hooks": { "FutureEvent": [ { "matcher": "x", "hooks": [ { "type": "command", "command": "a" } ] } ] },
              "unknownKey": { "nested": [1, 2] }
            }
            """;
        var doc = ClaudeSettingsDocument.TryParse(json, out _)!;
        doc.SetPermissions("allow", [.. doc.GetPermissions("allow"), PermissionRule.Compose("Bash", "git status:*")]);
        doc.SetPermissions("ask", [PermissionRule.Compose("WebFetch", "domain:example.com")]);
        var hooks = doc.GetHooks().ToList();
        hooks.Add(new HookHandlerModel("PreToolUse", HookMatcher.Compose(["Read", "Write"]), "python g.py", 5));
        doc.SetHooks(hooks);

        var saved = JsonDocument.Parse(doc.ToJson()).RootElement;

        Assert.Equal("opus", saved.GetProperty("model").GetString());
        Assert.Equal("plan", saved.GetProperty("permissions").GetProperty("defaultMode").GetString());
        Assert.Equal(2, saved.GetProperty("unknownKey").GetProperty("nested").GetArrayLength());
        Assert.Equal(["FutureTool(x)", "Bash(foo", "Bash(git status:*)"], saved.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal("WebFetch(domain:example.com)", saved.GetProperty("permissions").GetProperty("ask")[0].GetString());
        Assert.Equal("Read(./.env)", saved.GetProperty("permissions").GetProperty("deny")[0].GetString());
        Assert.Equal("x", saved.GetProperty("hooks").GetProperty("FutureEvent")[0].GetProperty("matcher").GetString());
        Assert.Equal("Read|Write", saved.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());

        var issues = doc.Validate(tools: ClaudeToolCatalog.LoadClaude());
        Assert.Contains(issues, i => i.Message.Contains("FutureEvent", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("FutureTool", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_command_hooks_do_not_gain_a_command_key()
    {
        const string json = """
            {"hooks":{"Stop":[{"hooks":[
              {"type":"http","url":"http://localhost:1/x","timeout":3},
              {"type":"prompt","prompt":"check"}]}]}}
            """;
        var doc = ClaudeSettingsDocument.TryParse(json, out _)!;
        doc.SetHooks(doc.GetHooks());

        var handlers = JsonDocument.Parse(doc.ToJson()).RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks");
        Assert.False(handlers[0].TryGetProperty("command", out _));
        Assert.Equal("http://localhost:1/x", handlers[0].GetProperty("url").GetString());
        Assert.Equal(3, handlers[0].GetProperty("timeout").GetInt32());
        Assert.False(handlers[1].TryGetProperty("command", out _));
        Assert.Equal("check", handlers[1].GetProperty("prompt").GetString());
    }

    [Fact]
    public void Matcher_on_an_event_without_matchers_is_noted_not_removed()
    {
        var doc = ClaudeSettingsDocument.TryParse("{\"hooks\":{\"Stop\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"echo\"}]}]}}", out _)!;
        Assert.Contains(doc.Validate(), i => i.Message.Contains("matcher를 사용하지 않습니다", StringComparison.Ordinal));
        doc.SetHooks(doc.GetHooks());
        Assert.Contains("\"matcher\": \"Bash\"", doc.ToJson(), StringComparison.Ordinal);
    }
}

public sealed class McpServerDiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, string text)
    {
        var path = _temp.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Servers_come_from_user_local_project_and_plugin_files_without_values()
    {
        var home = _temp.Combine("home");
        var project = _temp.Combine("work", "web");
        var projectKey = project.Replace('\\', '/');
        Write("home/.claude.json", $$"""
            { "mcpServers": { "github": { "command": "npx", "env": { "GITHUB_TOKEN": "ghp_SECRET1" } } },
              "projects": {
                "{{projectKey}}": { "mcpServers": { "db.local": { "url": "https://x", "headers": { "Authorization": "Bearer SECRET2" } } } },
                "C:/other": { "mcpServers": { "elsewhere": {} } } } }
            """);
        Write("work/web/.mcp.json", """{ "mcpServers": { "shared server": { "command": "run", "args": ["--key", "SECRET3"] } } }""");
        Write("home/.claude/plugins/cache/market/tools/1.0.0/.mcp.json", """{ "mcpServers": { "search": { "env": { "K": "SECRET4" } } } }""");
        Write("home/.claude/plugins/cache/market/tools/1.1.0/.claude-plugin/plugin.json", """{ "name": "tools", "mcpServers": { "search": {} } }""");

        var result = new McpServerDiscovery(PhysicalFileStore.Instance, home).Discover(project);

        Assert.Contains(result.Servers, s => s.Name == "github" && s.Source.StartsWith("사용자", StringComparison.Ordinal));
        Assert.Contains(result.Servers, s => s.Name == "db_local" && s.Source.StartsWith("로컬", StringComparison.Ordinal));
        Assert.Contains(result.Servers, s => s.Name == "shared_server" && s.Source.StartsWith("프로젝트", StringComparison.Ordinal));
        Assert.Single(result.Servers, s => s.Name == "plugin_tools_search");
        Assert.DoesNotContain(result.Servers, s => s.Name == "elsewhere");
        Assert.Equal("mcp__github", result.Servers.First(s => s.Name == "github").PermissionRule);
        Assert.Equal("mcp__github__.*", result.Servers.First(s => s.Name == "github").MatcherPattern);
        Assert.Empty(result.Problems);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("npx", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void User_settings_skip_project_sources_and_missing_or_broken_files_are_reported_safely()
    {
        var home = _temp.Combine("home");
        Write("home/.claude.json", "{ \"mcpServers\": { \"a\": { \"env\": { \"K\": \"SECRET5\" } }, ");
        var discovery = new McpServerDiscovery(PhysicalFileStore.Instance, home, _temp.Combine("managed-mcp.json"));

        var result = discovery.Discover(projectRoot: null);

        Assert.Empty(result.Servers);
        Assert.Single(result.Problems);
        Assert.DoesNotContain("SECRET", result.Problems[0], StringComparison.Ordinal);
        Assert.Empty(new McpServerDiscovery(PhysicalFileStore.Instance, _temp.Combine("nohome")).Discover(_temp.Combine("noproject")).Servers);
    }

    [Fact]
    public void Managed_file_is_read_when_given()
    {
        var managed = Write("ProgramFiles/managed-mcp.json", """{ "mcpServers": { "corp": {} } }""");
        var result = new McpServerDiscovery(PhysicalFileStore.Instance, _temp.Combine("home"), managed).Discover(null);
        Assert.Contains(result.Servers, s => s.Name == "corp" && s.Source.StartsWith("관리형", StringComparison.Ordinal));
    }
}

/// <summary>Regressions for the step B review (FileChanged file names, pipe-only names, raw matchers, MCP edge cases).</summary>
public sealed class StepBReviewRegressionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FileChanged_lists_literal_file_names_without_anchoring()
    {
        var syntax = HookMatcher.SyntaxOf(HookEventCatalog.BuiltIn.Find("FileChanged"));
        Assert.Equal(HookMatcherSyntax.FileNames, syntax);

        var empty = HookMatcher.Parse(string.Empty, syntax);
        var one = empty.Toggle(".envrc", on: true);
        Assert.Equal(".envrc", one);
        var two = HookMatcher.Parse(one, syntax).Toggle(".env", on: true);
        Assert.Equal(".envrc|.env", two);

        var back = HookMatcher.Parse(two, syntax);
        Assert.Equal(HookMatcherMode.List, back.Mode);
        Assert.True(back.Contains(".envrc") && back.Contains(".env"));
        Assert.Null(back.RegexError());
        Assert.NotNull(HookMatcher.Parse("data[1.txt", syntax).RegexError());
        Assert.Equal(HookMatcherMode.List, HookMatcher.Parse("data[1.txt", syntax).Mode);
    }

    [Theory]
    [InlineData("rate-limit")]
    [InlineData("rate_limit overloaded")]
    [InlineData("rate_limit,overloaded")]
    [InlineData(" rate_limit")]
    [InlineData("rate_limit ")]
    public void Pipe_only_events_take_the_regex_path_for_hyphens_spaces_and_commas(string raw)
    {
        Assert.Equal(HookMatcherMode.Regex, HookMatcher.Parse(raw, HookMatcherSyntax.PipeOnly).Mode);
        Assert.Equal(HookMatcherMode.List, HookMatcher.Parse("rate_limit|overloaded", HookMatcherSyntax.PipeOnly).Mode);
        Assert.Equal(HookMatcherSyntax.PipeOnly, HookMatcher.SyntaxOf(HookEventCatalog.BuiltIn.Find("StopFailure")));
    }

    [Fact]
    public void Unchanged_matcher_spaces_are_kept_when_hooks_are_written_back()
    {
        var doc = ClaudeSettingsDocument.TryParse("""{"hooks":{"PreToolUse":[{"matcher":" ^Bash$ ","hooks":[{"type":"command","command":"a"}]}]}}""", out _)!;
        doc.SetHooks(doc.GetHooks());

        var matcher = JsonDocument.Parse(doc.ToJson()).RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString();
        Assert.Equal(" ^Bash$ ", matcher);
    }

    [Fact]
    public void A_non_object_user_file_is_reported_and_other_sources_are_still_read()
    {
        var home = _temp.Combine("home");
        var project = _temp.Combine("work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(home, ".claude.json"), "[]");
        File.WriteAllText(Path.Combine(project, ".mcp.json"), """{ "mcpServers": { "shared": {} } }""");

        var result = new McpServerDiscovery(PhysicalFileStore.Instance, home).Discover(project);

        Assert.Contains(result.Servers, s => s.Name == "shared");
        Assert.Single(result.Problems);
        Assert.Contains(".claude.json", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_server_names_replace_every_character_outside_the_tool_name_set()
    {
        var home = _temp.Combine("home");
        var file = _temp.Combine("home", ".claude", "plugins", "cache", "m", "my.plugin", "1.0.0", ".mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{ "mcpServers": { "db+api": {}, "a-b": {} } }""");

        var names = new McpServerDiscovery(PhysicalFileStore.Instance, home).Discover(null).Servers.Select(s => s.Name).ToList();

        Assert.Contains("plugin_my_plugin_db_api", names);
        Assert.Contains("plugin_my_plugin_a-b", names);
    }
}
