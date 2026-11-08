using System.Text.Json.Nodes;
using DotDeck.App.ViewModels.Editor;
using DotDeck.Core.Editors;
using DotDeck.Core.Mcp;

namespace DotDeck.App.Tests;

/// <summary>
/// The settings form's permission table with fake MCP and agent discovery (no real files are read: the paths below
/// are only labels).
/// </summary>
public sealed class SettingsFormPermissionTests
{
    private static readonly McpServerInfo[] s_servers =
    [
        new("github", "사용자 (~/.claude.json)", @"C:\fake\home\.claude.json"),
        new("docs_site", "프로젝트 (.mcp.json)", @"C:\fake\project\.mcp.json"),
        new("plugin_tools_db", "플러그인 tools@market (설치됨, 사용 여부는 enabledPlugins 확인)", @"C:\fake\home\.claude\plugins\cache\market\tools\1.0\.mcp.json"),
    ];

    private readonly List<string> _output = [];
    private IReadOnlyList<McpServerInfo> _servers = s_servers;

    private SettingsFormViewModel CreateForm(string text = "{}")
    {
        var form = new SettingsFormViewModel(_output.Add)
        {
            Tools = ClaudeToolCatalog.LoadClaude(),
            Events = HookEventCatalog.BuiltIn,
            DiscoverMcp = _ => Task.FromResult(new McpDiscoveryResult(_servers, [@"JSON 형식이 올바르지 않음: C:\fake\broken.json"])),
            DiscoverAgents = _ => Task.FromResult<IReadOnlyList<AgentNameInfo>>(
                [new AgentNameInfo("reviewer", "프로젝트", @"C:\fake\project\.claude\agents\reviewer.md")]),
        };
        Assert.Null(form.Load(text));
        return form;
    }

    [Fact]
    public async Task Found_mcp_servers_appear_in_permission_and_matcher_choices_and_follow_refresh()
    {
        var form = CreateForm("""{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "x" } ] } ] } }""");
        await form.RefreshMcpCommand.ExecuteAsync(null);

        var mcpTools = form.ToolChoices.Where(c => c.Group == SettingsFormViewModel.McpGroup).ToList();
        Assert.Equal(["mcp__github", "mcp__docs_site", "mcp__plugin_tools_db"], mcpTools.Select(c => c.Value));
        Assert.Contains(@"C:\fake\project\.mcp.json", mcpTools[1].Description, StringComparison.Ordinal);

        var matcher = form.Hooks.Single().MatcherChoices().Where(c => c.Group == SettingsFormViewModel.McpGroup).Select(c => c.Value).ToList();
        Assert.Equal(["mcp__github__.*", "mcp__docs_site__.*", "mcp__plugin_tools_db__.*"], matcher);

        Assert.Equal("MCP 서버 3개 · 읽지 못한 파일 1개", form.McpStatus);
        Assert.Single(form.McpProblems);
        form.ToggleMcpListCommand.Execute(null);
        Assert.True(form.IsMcpListOpen);

        _servers = [s_servers[0]];
        await form.RefreshMcpCommand.ExecuteAsync(null);

        Assert.Equal(["mcp__github"], form.ToolChoices.Where(c => c.Group == SettingsFormViewModel.McpGroup).Select(c => c.Value));
        Assert.Equal(["mcp__github__.*"], form.Hooks.Single().MatcherChoices().Where(c => c.Group == SettingsFormViewModel.McpGroup).Select(c => c.Value));
    }

    [Fact]
    public async Task Mcp_rows_pick_a_scope_instead_of_an_argument()
    {
        var form = CreateForm();
        await form.RefreshMcpCommand.ExecuteAsync(null);
        form.AddPermissionCommand.Execute("allow");
        var row = form.Permissions.Single();

        row.SetToolCommand.Execute("mcp__github");
        Assert.True(row.ShowMcpScope);
        Assert.False(row.ShowArgumentBox);
        Assert.Equal("github", row.McpServer);
        Assert.Equal("서버 전체", row.McpScopeLabel);

        row.SetMcpScopeCommand.Execute("all");
        Assert.Equal("mcp__github__*", row.Tool);

        row.McpToolName = "create_pr";
        Assert.Equal("mcp__github__create_pr", row.Tool);
        Assert.True(row.IsMcpToolScope);

        row.SetMcpScopeCommand.Execute("server");
        Assert.Equal("mcp__github", row.Tool);
        row.SetMcpScopeCommand.Execute("tool");
        Assert.Equal("mcp__github__create_pr", row.Tool);

        row.Tool = "mcp__docs_site__search";
        Assert.Equal("docs_site", row.McpServer);
        Assert.Equal("search", row.McpToolName);
        Assert.Empty(row.Issues);
    }

    [Fact]
    public async Task Whole_server_of_a_name_with_double_underscores_is_written_with_the_wildcard()
    {
        _servers = [new McpServerInfo("reports__v2", "프로젝트 (.mcp.json)", @"C:\fake\project\.mcp.json")];
        var form = CreateForm();
        await form.RefreshMcpCommand.ExecuteAsync(null);
        form.AddPermissionCommand.Execute("allow");
        var row = form.Permissions.Single();

        row.Tool = "mcp__reports__v2__export";
        Assert.Equal("reports__v2", row.McpServer);
        Assert.Equal("export", row.McpToolName);

        row.SetMcpScopeCommand.Execute("server");
        Assert.Equal("mcp__reports__v2__*", row.Tool);
        Assert.Empty(row.Issues);
    }

    [Fact]
    public void Argument_cells_follow_the_tool_argument_kind()
    {
        var form = CreateForm();
        form.AddPermissionCommand.Execute("allow");
        var row = form.Permissions.Single();

        row.Tool = "WebFetch";
        Assert.True(row.UsesPrefix);
        Assert.Equal("domain:", row.Prefix);
        row.ArgumentValue = "*.github.com";
        Assert.Equal("domain:*.github.com", row.Argument);
        Assert.Equal("*.github.com", row.ArgumentValue);
        Assert.Equal("WebFetch(domain:*.github.com)", row.RuleText);
        row.ArgumentValue = "domain:github.com";
        Assert.Equal("domain:github.com", row.Argument);

        row.Tool = "WebSearch";
        row.Argument = string.Empty;
        Assert.False(row.IsArgumentEnabled);
        Assert.Equal(ArgumentInput.Disabled, row.Input);

        row.Tool = "Bash";
        Assert.True(row.IsArgumentEnabled);
        Assert.Contains("git status:*", row.ArgumentPlaceholder, StringComparison.Ordinal);

        row.Tool = "Agent";
        Assert.True(row.HasArgumentChoices);
        Assert.Equal(["Explore", "Plan", "reviewer"], form.AgentChoices.Select(c => c.Value));
        row.SetArgumentCommand.Execute("reviewer");
        Assert.Equal("Agent(reviewer)", row.RuleText);
    }

    [Fact]
    public void Rules_with_warnings_show_them_and_are_saved_as_written()
    {
        const string text = """
            {
              "permissions": {
                "allow": [ "mcp__github(create_pr)", "WebSearch(cats)", "WebFetch(github.com)", "Bash(git add . && git commit)", "Bash(git push:*)" ],
                "deny": [ "Edit(./secrets/**)", "Bash(git push:*)", "Bash(command:rm *)" ],
                "future": true
              },
              "unknownTop": [ 1, 2 ]
            }
            """;
        var form = CreateForm(text);

        string[] warned = ["mcp__github(create_pr)", "WebSearch(cats)", "WebFetch(github.com)", "Bash(command:rm *)"];
        foreach (var raw in warned)
        {
            Assert.True(form.Permissions.Single(p => p.RuleText == raw).HasWarnings, raw);
        }

        Assert.True(form.Permissions.Single(p => p.RuleText == "Bash(git add . && git commit)").HasNotesOnly);
        Assert.True(form.Permissions.Single(p => p.RuleText == "Edit(./secrets/**)").HasNotesOnly);
        Assert.All(form.Permissions.Where(p => p.RuleText == "Bash(git push:*)"), p => Assert.True(p.HasWarnings));
        Assert.True(form.Permissions.Single(p => p.RuleText == "WebFetch(github.com)").ShowArgumentBox);
        Assert.False(form.Permissions.Single(p => p.RuleText == "WebFetch(github.com)").UsesPrefix);
        Assert.True(form.Permissions.Single(p => p.RuleText == "mcp__github(create_pr)").ShowArgumentBox);

        // An unrelated edit writes the document; every rule with a warning is kept exactly.
        form.AddPermissionCommand.Execute("ask");
        form.Permissions.Last().Tool = "Read";

        var saved = JsonNode.Parse(_output.Last())!;
        var original = JsonNode.Parse(text)!;
        Assert.True(JsonNode.DeepEquals(original["permissions"]!["allow"], saved["permissions"]!["allow"]));
        Assert.True(JsonNode.DeepEquals(original["permissions"]!["deny"], saved["permissions"]!["deny"]));
        Assert.Equal("Read", (string?)saved["permissions"]!["ask"]![0]);
        Assert.True((bool)saved["permissions"]!["future"]!);
        Assert.True(JsonNode.DeepEquals(original["unknownTop"], saved["unknownTop"]));
    }

    [Fact]
    public void Row_warnings_update_while_editing()
    {
        var form = CreateForm();
        form.AddPermissionCommand.Execute("allow");
        var row = form.Permissions.Single();

        row.Tool = "WebFetch";
        row.Argument = "github.com";
        Assert.Contains("domain:", row.IssueText, StringComparison.Ordinal);

        row.Argument = "domain:github.com";
        Assert.Empty(row.Issues);

        row.Tool = "Agent";
        row.Argument = "model:opus";
        Assert.True(row.HasWarnings);
        row.Kind = "deny";
        Assert.Empty(row.Issues);
        Assert.NotNull(row.Parameter);
    }
}
