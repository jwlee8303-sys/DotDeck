using DotDeck.Core.Editors;

namespace DotDeck.Core.Tests.Editors;

public sealed class EditorValidatorTests
{
    [Theory]
    [InlineData(@"C:\h\.claude\agents\rev.md", EditorKind.Agent)]
    [InlineData(@"C:\h\.claude\skills\pdf\SKILL.md", EditorKind.Skill)]
    [InlineData(@"C:\h\.claude\settings.json", EditorKind.ClaudeSettings)]
    [InlineData(@"C:\p\.claude\settings.local.json", EditorKind.ClaudeSettings)]
    [InlineData(@"C:\p\other\settings.json", EditorKind.Text)]
    [InlineData(@"C:\h\.claude\CLAUDE.md", EditorKind.Markdown)]
    [InlineData(@"C:\h\.codex\config.toml", EditorKind.Text)]
    public void Detects_editor_kind(string path, EditorKind kind)
    {
        Assert.Equal(kind, EditorKinds.Detect(path));
    }

    [Fact]
    public void Valid_agent_has_no_issues()
    {
        Assert.Empty(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\rev.md", "---\nname: rev\ndescription: d\nmodel: sonnet\n---\nbody"));
    }

    [Fact]
    public void Agent_problems_are_reported()
    {
        var issues = MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\rev.md", "---\nname: other\nmodel: gpt\n---\n");

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("파일 이름", StringComparison.Ordinal) && i.Line == 2);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("description", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("gpt", StringComparison.Ordinal));
        Assert.Contains(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\x.md", "no frontmatter"), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Skill_name_must_match_folder_and_description_is_limited()
    {
        var longText = new string('x', MarkdownAgentValidator.MaxSkillDescription + 1);
        var issues = MarkdownAgentValidator.ValidateSkill(@"C:\s\skills\pdf\SKILL.md", $"---\nname: docs\ndescription: {longText}\n---\n");

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("폴더 이름", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("1024", StringComparison.Ordinal));
        Assert.Empty(MarkdownAgentValidator.ValidateSkill(@"C:\s\skills\pdf\SKILL.md", "---\nname: pdf\ndescription: ok\n---\n"));
    }
}
