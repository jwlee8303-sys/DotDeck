using DotDeck.Core.Editors;
using DotDeck.Core.Editors.Registry;
using DotDeck.Core.Files;
using DotDeck.Core.Roles;

namespace DotDeck.Core.Tests.Editors;

public sealed class EditorRegistryTests
{
    [Theory]
    [InlineData(EditorKind.Text, "text")]
    [InlineData(EditorKind.Markdown, "markdown")]
    [InlineData(EditorKind.Agent, "agentForm")]
    [InlineData(EditorKind.Skill, "skillForm")]
    [InlineData(EditorKind.ClaudeSettings, "settingsForm")]
    [InlineData(EditorKind.CodexConfig, "codexConfigForm")]
    [InlineData(EditorKind.CodexHooks, "codexHooksForm")]
    [InlineData(EditorKind.CodexAgent, "codexAgentForm")]
    [InlineData(EditorKind.CodexSkillMetadata, "openaiYamlForm")]
    [InlineData(EditorKind.CodexRules, "codexRulesForm")]
    public void Persistence_and_catalog_use_the_same_stable_names(EditorKind kind, string name)
    {
        Assert.Equal(name, EditorOverrideStore.KindToName(kind));
        Assert.Equal(kind, EditorOverrideStore.NameToKind(name));
        Assert.Equal(kind, FileRoleCatalog.EditorOf(name));
    }

    [Fact]
    public void Unknown_names_remain_unrecognized_and_open_as_text()
    {
        Assert.Null(EditorOverrideStore.NameToKind("futureForm"));
        Assert.Null(EditorOverrideStore.NameToKind("SettingsForm"));
        Assert.Equal(EditorKind.Text, FileRoleCatalog.EditorOf("futureForm"));
        Assert.Equal(EditorKind.Text, FileRoleCatalog.EditorOf(null));
    }

    [Fact]
    public void Capability_checks_reject_bad_input_without_rejecting_semantic_warnings()
    {
        var settings = EditorRegistry.Get(EditorKind.ClaudeSettings);
        Assert.False(settings.CanShowForm("[]", out var jsonError));
        Assert.NotNull(jsonError);
        Assert.False(settings.CanShowForm("{broken", out _));
        Assert.True(settings.CanShowForm("{}", out _));

        var agent = EditorRegistry.Get(EditorKind.Agent);
        Assert.True(agent.CanShowForm("plain markdown", out _));
        var context = new EditorValidationContext(@"C:\fake\agent.md", "plain markdown", PhysicalFileStore.Instance);
        Assert.Contains(agent.Validate!(context), i => i.Severity == IssueSeverity.Error);
        Assert.False(EditorRegistry.Get(EditorKind.CodexConfig).CanShowForm("", out _));
    }

    [Fact]
    public void Registered_validators_keep_claude_diagnostics()
    {
        var context = new EditorValidationContext(@"C:\fake\settings.json", "{broken", PhysicalFileStore.Instance);
        Assert.Contains(EditorRegistry.Get(EditorKind.ClaudeSettings).Validate!(context), i => i.Severity == IssueSeverity.Error);
        var text = "---\nname: rev\ndescription: d\nmodel: sonnet\n---\nbody";
        Assert.Empty(EditorRegistry.Get(EditorKind.Agent).Validate!(context with { Path = @"C:\fake\rev.md", Text = text }));
    }
}
