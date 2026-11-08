using DotDeck.Core.Editors;

namespace DotDeck.Core.Tests.Editors;

public sealed class EditorValidatorTests
{
    [Fact]
    public void Valid_agent_has_no_issues()
    {
        Assert.Empty(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\rev.md", "---\nname: rev\ndescription: d\nmodel: sonnet\n---\nbody"));
    }

    [Fact]
    public void Agent_problems_are_reported()
    {
        var issues = MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\rev.md", "---\nname: other\nmodel: gpt\n---\n");

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("파일 이름", StringComparison.Ordinal) && i.Line == 2);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("description", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("gpt", StringComparison.Ordinal));
        Assert.Contains(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\x.md", "no frontmatter"), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\x.md", "---\nname: a:b\ndescription: d\n---\n"), i => i.Severity == IssueSeverity.Error);
        Assert.Empty(MarkdownAgentValidator.ValidateAgent(@"C:\a\agents\x.md", "---\nname: x\ndescription: d\nmodel: fable\n---\n"));
    }

    [Fact]
    public void Skill_name_defaults_to_folder_and_description_is_limited()
    {
        var longText = new string('x', MarkdownAgentValidator.MaxSkillDescription - 10);
        var issues = MarkdownAgentValidator.ValidateSkill(@"C:\s\skills\pdf\SKILL.md",
            $"---\nname: docs\ndescription: {longText}\nwhen_to_use: more than ten chars\n---\n");

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("폴더 이름", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("1536", StringComparison.Ordinal));
        Assert.Empty(MarkdownAgentValidator.ValidateSkill(@"C:\s\skills\pdf\SKILL.md", "---\nname: pdf\ndescription: ok\n---\n"));
        var unnamed = MarkdownAgentValidator.ValidateSkill(@"C:\s\skills\pdf\SKILL.md", "---\ndescription: ok\n---\n");
        Assert.All(unnamed, i => Assert.Equal(IssueSeverity.Info, i.Severity));
    }
}
