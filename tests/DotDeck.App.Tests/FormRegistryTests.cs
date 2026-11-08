using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Editor;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Editors;
using DotDeck.Core.Editors.Registry;
using DotDeck.Core.Mcp;

namespace DotDeck.App.Tests;

public sealed class FormRegistryTests
{
    [Fact]
    public void Menus_offer_only_implemented_editors_and_route_to_the_supplied_command()
    {
        string? chosen = null;
        var command = new RelayCommand<string>(name => chosen = name);
        var choices = FormRegistry.Choices(command);
        Assert.Equal(["text", "markdown", "settingsForm", "agentForm", "skillForm", "codexConfigForm", "auto"], choices.Select(c => c.Name));
        foreach (var choice in choices)
        {
            choice.Command.Execute(choice.Name);
            Assert.Equal(choice.Name, chosen);
        }

        foreach (var descriptor in EditorRegistry.All)
        {
            Assert.Equal(descriptor.HasForm, FormRegistry.HasForm(descriptor.Kind));
        }
    }

    [Fact]
    public async Task Settings_factory_keeps_services_unknown_values_and_single_apply()
    {
        var host = new FakeHost();
        var writes = new List<string>();
        var form = Assert.IsType<SettingsFormViewModel>(FormRegistry.Create(EditorKind.ClaudeSettings,
            new FormContext(writes.Add, @"C:\fake\settings.json", null, host)));
        Assert.Same(host.ToolCatalog, form.Tools);
        Assert.Same(host.HookEvents, form.Events);
        Assert.Null(form.Load("""{"unknown":{"keep":true},"permissions":{"allow":["Bash(git:*)"]}}"""));
        Assert.Empty(writes);
        var initialDiscoveries = host.DiscoveryCount;
        await form.RefreshMcpCommand.ExecuteAsync(null);
        Assert.Equal(initialDiscoveries + 1, host.DiscoveryCount);
        Assert.Empty(writes);
        form.Permissions.Single().Argument = "dotnet:*";
        var saved = JsonNode.Parse(Assert.Single(writes))!;
        Assert.True(saved["unknown"]!["keep"]!.GetValue<bool>());
        Assert.Equal("Bash(dotnet:*)", saved["permissions"]!["allow"]![0]!.GetValue<string>());
        form.ShowHelp!(null);
        Assert.Equal(1, host.HelpCount);
    }

    [Theory]
    [InlineData(EditorKind.Agent, true)]
    [InlineData(EditorKind.Skill, false)]
    public void Frontmatter_factory_preserves_profile_unknown_fields_and_body(EditorKind kind, bool isAgent)
    {
        var writes = new List<string>();
        var form = Assert.IsType<FrontmatterFormViewModel>(FormRegistry.Create(kind,
            new FormContext(writes.Add, @"C:\fake\doc.md", null, new FakeHost())));
        Assert.Equal(isAgent, form.IsAgent);
        Assert.Null(form.Load("---\nname: before\ndescription: d\nunknown: keep\n---\nbody"));
        Assert.Empty(writes);
        form.Name = "after";
        var saved = Assert.Single(writes);
        Assert.Contains("unknown: keep", saved, StringComparison.Ordinal);
        Assert.EndsWith("body", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Unimplemented_forms_do_not_fall_back_to_a_claude_form()
    {
        Assert.Null(FormRegistry.Create(EditorKind.CodexHooks,
            new FormContext(_ => Assert.Fail("Must not edit"), @"C:\fake\config.toml", null, new FakeHost())));
    }

    private sealed class FakeHost : ITextDocumentHost, IClaudeFormServices
    {
        public SettingsKeyCatalog? SettingsKeys => null;
        public ClaudeToolCatalog ToolCatalog { get; } = ClaudeToolCatalog.LoadClaude();
        public HookEventCatalog HookEvents { get; } = HookEventCatalog.BuiltIn;
        public int DiscoveryCount { get; private set; }
        public int HelpCount { get; private set; }
        public Task SaveAsync(TextDocumentViewModel document) => Task.CompletedTask;
        public void OpenNewView(TextDocumentViewModel document) { }
        public void SetEditorKind(TextDocumentViewModel document, EditorKind? kind) { }
        public void RunHook(HookRow row) { }
        public void ShowFormHelp(RoleDescription? help) => HelpCount++;
        public Task<McpDiscoveryResult> DiscoverMcpAsync(string? projectRoot)
        {
            DiscoveryCount++;
            return Task.FromResult(new McpDiscoveryResult([], []));
        }

        public Task<IReadOnlyList<AgentNameInfo>> DiscoverAgentsAsync(string? projectRoot) => Task.FromResult<IReadOnlyList<AgentNameInfo>>([]);
    }
}
