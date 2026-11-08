using System.Text.Json.Nodes;
using DotDeck.Core.Editors;
using DotDeck.Core.Files;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.Editors;

public sealed class PermissionArgumentKindTests
{
    private static readonly ClaudeToolCatalog s_tools = ClaudeToolCatalog.LoadClaude();

    [Theory]
    [InlineData("Bash(git status:*)", "Bash", "git status:*", "command")]
    [InlineData("Read(//**/.env)", "Read", "//**/.env", "path")]
    [InlineData("WebFetch(domain:*.github.com)", "WebFetch", "domain:*.github.com", "domain")]
    [InlineData("Agent(Explore)", "Agent", "Explore", "agent")]
    [InlineData("WebSearch", "WebSearch", null, "none")]
    [InlineData("mcp__github", "mcp__github", null, "mcp")]
    [InlineData("mcp__github__*", "mcp__github__*", null, "mcp")]
    [InlineData("mcp__github__create_pr", "mcp__github__create_pr", null, "mcp")]
    public void Rules_split_by_argument_kind_and_compose_back(string raw, string tool, string? argument, string kind)
    {
        var rule = PermissionRule.Parse(raw);

        Assert.True(rule.IsParsed);
        Assert.Equal(tool, rule.Tool);
        Assert.Equal(argument, rule.Argument);
        Assert.Equal(kind, s_tools.KindKeyOf(rule.Tool));
        Assert.Null(PermissionRule.ParameterOf(rule.Tool, rule.Argument, s_tools));
        Assert.Equal(raw, PermissionRule.Compose(rule.Tool, rule.Argument));
    }

    [Theory]
    [InlineData("mcp__github", "github", McpRuleScope.Server, "")]
    [InlineData("mcp__github__*", "github", McpRuleScope.AllTools, "")]
    [InlineData("mcp__github__create_pr", "github", McpRuleScope.Tool, "create_pr")]
    public void Mcp_names_split_into_server_and_scope(string tool, string server, McpRuleScope scope, string name)
    {
        var parts = McpRuleParts.Parse(tool);

        Assert.Equal(new McpRuleParts(server, scope, name), parts);
        Assert.Equal(tool, parts!.ToolText);
        Assert.Equal("mcp__github__*", (parts with { Scope = McpRuleScope.AllTools, ToolName = "" }).ToolText);
    }

    [Fact]
    public void Known_server_names_with_double_underscores_win()
    {
        var parts = McpRuleParts.Parse("mcp__plugin_a__b__list", ["plugin_a__b", "plugin_a"]);

        Assert.Equal(new McpRuleParts("plugin_a__b", McpRuleScope.Tool, "list"), parts);
        Assert.Null(McpRuleParts.Parse("mcp__"));
        Assert.Null(McpRuleParts.Parse("Bash"));
    }

    [Fact]
    public void Server_names_with_double_underscores_use_the_wildcard_form_for_the_whole_server()
    {
        Assert.Equal("mcp__reports__v2__*", new McpRuleParts("reports__v2", McpRuleScope.Server).ToolText);
        Assert.Equal("mcp__github", new McpRuleParts("github", McpRuleScope.Server).ToolText);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    public void Catalog_with_null_list_items_or_empty_separators_is_rejected(string separators)
    {
        var json = JsonNode.Parse(DotDeck.Core.Catalogs.CatalogResources.ReadBuiltIn(ClaudeToolCatalog.ClaudeResource))!.AsObject();
        json["argumentKinds"]!["command"]!["separators"] = JsonNode.Parse(separators);

        Assert.Throws<InvalidDataException>(() => ClaudeToolCatalog.Parse(json.ToJsonString()));

        json["argumentKinds"]!["command"]!["separators"] = new JsonArray("&&");
        json["argumentKinds"]!["path"]!["help"] = JsonNode.Parse("[null]");
        Assert.Throws<InvalidDataException>(() => ClaudeToolCatalog.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("Agent", "model:opus", "model", false)]
    [InlineData("Bash", "run_in_background:true", "run_in_background", false)]
    [InlineData("Bash", "command:rm *", "command", true)]
    [InlineData("WebFetch", "url:https://x", "url", true)]
    public void Parameter_rules_are_recognized_only_for_listed_parameters(string tool, string argument, string name, bool excluded)
    {
        var parameter = PermissionRule.ParameterOf(tool, argument, s_tools);

        Assert.NotNull(parameter);
        Assert.Equal(name, parameter.Name);
        Assert.Equal(excluded, parameter.IsExcluded);
    }

    [Theory]
    [InlineData("Bash", "git:*")]
    [InlineData("WebFetch", "domain:github.com")]
    [InlineData("Agent", "Explore")]
    [InlineData("FutureTool", "model:x")]
    public void Ordinary_arguments_are_not_parameter_rules(string tool, string argument)
    {
        Assert.Null(PermissionRule.ParameterOf(tool, argument, s_tools));
    }

    private static List<PermissionIssue> Check(params (string Kind, string Raw)[] rules) =>
        [.. PermissionLint.Check([.. rules.Select(r => new PermissionEntry(r.Kind, r.Raw))], s_tools)];

    [Theory]
    [InlineData("allow", "mcp__github(create_pr)", "괄호를 쓰지 않습니다", IssueSeverity.Warning)]
    [InlineData("deny", "WebSearch(cats)", "보통 이름만", IssueSeverity.Warning)]
    [InlineData("allow", "WebFetch(github.com)", "domain:호스트", IssueSeverity.Warning)]
    [InlineData("allow", "Bash(git add . && git commit)", "복합 명령", IssueSeverity.Info)]
    [InlineData("allow", "Bash(make; rm -rf x)", "복합 명령", IssueSeverity.Info)]
    [InlineData("allow", "Bash(cat a | grep b)", "복합 명령", IssueSeverity.Info)]
    [InlineData("allow", "PowerShell(Get-Item a | Remove-Item)", "복합 명령", IssueSeverity.Info)]
    [InlineData("allow", "Bash(git:* push)", ":*는 끝에서만", IssueSeverity.Info)]
    [InlineData("allow", "Agent(model:opus)", "deny·ask에서만", IssueSeverity.Warning)]
    [InlineData("deny", "Bash(command:rm *)", "command은(는) 매개변수 규칙에 쓸 수 없어", IssueSeverity.Warning)]
    [InlineData("deny", "Edit(./secrets/**)", "읽기는 막혀 있지 않습니다", IssueSeverity.Info)]
    [InlineData("allow", "Write(src/**)", "Edit(경로)", IssueSeverity.Warning)]
    public void Each_warning_fires_once_for_its_rule(string kind, string raw, string text, IssueSeverity severity)
    {
        var issues = Check((kind, raw));

        var issue = Assert.Single(issues);
        Assert.Contains(text, issue.Issue.Message, StringComparison.Ordinal);
        Assert.Equal(severity, issue.Issue.Severity);
        Assert.Equal(0, issue.Index);
    }

    [Fact]
    public void Correct_rules_raise_nothing()
    {
        var issues = Check(
            ("allow", "Bash(git status:*)"),
            ("allow", "Bash(npm run test *)"),
            ("allow", "PowerShell(Get-ChildItem *)"),
            ("deny", "Read(//**/.env)"),
            ("allow", "WebFetch(domain:*.github.com)"),
            ("allow", "WebFetch"),
            ("allow", "Agent(Explore)"),
            ("allow", "Agent"),
            ("allow", "WebSearch"),
            ("allow", "mcp__github"),
            ("allow", "mcp__github__*"),
            ("allow", "mcp__github__create_pr"),
            ("deny", "mcp__*"),
            ("deny", "Agent(model:opus)"),
            ("ask", "Bash(run_in_background:true)"),
            ("allow", "Skill(deploy *)"),
            ("deny", "Skill(skill:release)"));

        Assert.Empty(issues);
    }

    [Fact]
    public void Edit_deny_is_covered_by_a_read_deny_of_the_same_path_or_all_reads()
    {
        Assert.Empty(Check(("deny", "Edit(./secrets/**)"), ("deny", "Read(./secrets/**)")));
        Assert.Empty(Check(("deny", "Edit(./secrets/**)"), ("deny", "Read")));
        Assert.Contains("Read(./secrets/**)", Assert.Single(Check(("deny", "Edit(./secrets/**)"), ("allow", "Read(./secrets/**)"))).Issue.Message,
            StringComparison.Ordinal);
        Assert.Contains("deny에 Read를", Assert.Single(Check(("deny", "Edit"))).Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allow_and_deny_conflict_marks_both_rules()
    {
        var issue = Assert.Single(Check(("allow", "Bash(git push:*)"), ("deny", "Bash(git push:*)")));

        Assert.Equal(IssueSeverity.Error, issue.Issue.Severity);
        Assert.Equal("권한 규칙이 allow와 deny에 모두 있습니다: Bash(git push:*)", issue.Issue.Message);
        Assert.Equal(1, issue.Index);
        Assert.Equal(0, issue.OtherIndex);
    }

    [Fact]
    public void Rules_with_warnings_are_kept_verbatim_when_written_back()
    {
        const string text = """
            {
              "permissions": {
                "allow": [ "mcp__github(create_pr)", "WebFetch(github.com)", "Bash(git add . && git commit)", "Agent(model:opus)" ],
                "deny": [ "Edit(./secrets/**)", "Bash(command:rm *)", "WebSearch(cats)" ],
                "futureList": [ "x" ]
              },
              "futureKey": { "a": 1 }
            }
            """;
        var doc = ClaudeSettingsDocument.TryParse(text, out _)!;

        var issues = doc.Validate(tools: s_tools);
        Assert.True(issues.Count >= 7);

        foreach (var kind in ClaudeSettingsDocument.PermissionKinds)
        {
            doc.SetPermissions(kind, doc.GetPermissions(kind));
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(doc.ToJson())));
    }

    [Fact]
    public void Catalog_needs_schema_2_with_every_message_and_kind()
    {
        Assert.Equal(2, s_tools.SchemaVersion);
        Assert.All(ClaudeToolCatalog.MessageKeys, k => Assert.False(string.IsNullOrWhiteSpace(s_tools.Messages[k])));
        Assert.Equal(["Explore", "Plan"], s_tools.ArgumentKinds["agent"].Choices);
        Assert.Equal("domain:", s_tools.ArgumentKinds["domain"].Prefix);
        Assert.Equal(ArgumentInput.Mcp, s_tools.ArgumentOf("mcp__x")!.Input);
        Assert.Equal(ArgumentInput.Disabled, s_tools.ArgumentOf("WebSearch")!.Input);
        Assert.Equal("command", s_tools.KindKeyOf("PowerShell"));
        Assert.Contains("&&", s_tools.SeparatorsOf("PowerShell"));
        Assert.DoesNotContain("&", s_tools.SeparatorsOf("PowerShell"));
        Assert.Contains("|&", s_tools.SeparatorsOf("Bash"));
        Assert.NotEmpty(s_tools.ArgumentKinds["mcp"].Examples);

        var json = JsonNode.Parse(CatalogText())!.AsObject();
        json["schemaVersion"] = 1;
        Assert.Throws<InvalidDataException>(() => ClaudeToolCatalog.Parse(json.ToJsonString()));
        json["schemaVersion"] = 2;
        json["messages"]!.AsObject().Remove("compound");
        Assert.Throws<InvalidDataException>(() => ClaudeToolCatalog.Parse(json.ToJsonString()));
    }

    [Fact]
    public void Help_shows_the_argument_kind_and_parameter_rules()
    {
        Assert.Contains(s_tools.PermissionHelp("Agent", null), l => l.Contains("model", StringComparison.Ordinal) && l.Contains("매개변수", StringComparison.Ordinal));
        Assert.Contains(s_tools.PermissionHelp("WebFetch", null), l => l.StartsWith("인수 형식: 도메인", StringComparison.Ordinal));
        Assert.Contains(s_tools.PermissionHelp("mcp__github", null), l => l.Contains("mcp__github__create_pr", StringComparison.Ordinal));
        Assert.Contains(s_tools.ParameterHelp(), l => l.Contains("Agent(model:opus)", StringComparison.Ordinal));
    }

    private static string CatalogText() => DotDeck.Core.Catalogs.CatalogResources.ReadBuiltIn(ClaudeToolCatalog.ClaudeResource);
}

public sealed class AgentNameFinderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private void Write(string text, params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Names_come_from_frontmatter_or_file_names_for_the_scope()
    {
        Write("---\nname: reviewer\ndescription: x\n---\nbody", "home", ".claude", "agents", "code-review.md");
        Write("no frontmatter", "home", ".claude", "agents", "team", "helper.md");
        Write("not an agent", "home", ".claude", "agents", "notes.txt");
        Write("---\nname: builder\n---\n", "proj", ".claude", "agents", "build.md");
        Write("---\nname: reviewer\n---\n", "proj", ".claude", "agents", "review.md");
        var finder = new AgentNameFinder(PhysicalFileStore.Instance, _temp.Combine("home"));

        var user = finder.Find(null);
        Assert.Equal(["helper", "reviewer"], user.Select(a => a.Name));
        Assert.All(user, a => Assert.Equal("사용자", a.Source));

        var project = finder.Find(_temp.Combine("proj"));
        Assert.Equal(["builder", "helper", "reviewer"], project.Select(a => a.Name));
        Assert.Equal("프로젝트", project.Single(a => a.Name == "reviewer").Source);
    }

    [Theory]
    [InlineData(".claude")]
    [InlineData("agents")]
    public void Linked_claude_or_agents_folders_are_not_followed(string linked)
    {
        Write("---\nname: outside\n---\n", "home", ".claude", "agents", "a.md");
        var home = _temp.Combine("home");
        var target = linked == ".claude" ? Path.Combine(home, ".claude") : Path.Combine(home, ".claude", "agents");
        var finder = new AgentNameFinder(new LinkMarkingStore(target), home);

        Assert.Empty(finder.Find(null));
        Assert.Single(new AgentNameFinder(PhysicalFileStore.Instance, home).Find(null));
    }

    /// <summary>Reports one folder as a junction (creating real links needs privileges).</summary>
    private sealed class LinkMarkingStore(string linked) : IFileStore
    {
        private static readonly IFileStore s_inner = PhysicalFileStore.Instance;

        public FileSystemEntry? GetEntry(string path) =>
            s_inner.GetEntry(path) is { } entry && string.Equals(Path.TrimEndingDirectorySeparator(path), linked, StringComparison.OrdinalIgnoreCase)
                ? entry with { Attributes = entry.Attributes | FileAttributes.ReparsePoint }
                : s_inner.GetEntry(path);

        public bool FileExists(string path) => s_inner.FileExists(path);

        public bool DirectoryExists(string path) => s_inner.DirectoryExists(path);

        public IReadOnlyList<FileSystemEntry> GetChildren(string directory) => s_inner.GetChildren(directory);

        public byte[] ReadAllBytes(string path) => s_inner.ReadAllBytes(path);

        public string? ResolveLinkTarget(string path) => s_inner.ResolveLinkTarget(path);

        public byte[] ReadHead(string path, int maxBytes) => s_inner.ReadHead(path, maxBytes);

        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void CreateFile(string path) => throw new NotSupportedException();

        public void Move(string source, string destination, bool overwrite = false) => throw new NotSupportedException();

        public void Copy(string source, string destination) => throw new NotSupportedException();

        public void ReplaceFile(string source, string destination) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public IDisposable AcquireLock(string lockPath, TimeSpan timeout) => throw new NotSupportedException();
    }

    [Fact]
    public void Missing_folders_give_no_names()
    {
        Assert.Empty(new AgentNameFinder(PhysicalFileStore.Instance, _temp.Combine("nobody")).Find(_temp.Combine("none")));
    }
}
