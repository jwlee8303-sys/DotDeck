namespace DotDeck.Core.Editors;

/// <summary>
/// Checks of subagent files (<c>.claude/agents/**/*.md</c>) and skills (<c>.claude/skills/&lt;name&gt;/SKILL.md</c>),
/// following https://code.claude.com/docs/en/sub-agents and https://code.claude.com/docs/en/skills (2026-11-08).
/// </summary>
public static class MarkdownAgentValidator
{
    /// <summary>The skill listing truncates <c>description</c> plus <c>when_to_use</c> at this length.</summary>
    public const int MaxSkillDescription = 1536;

    public const int MaxAgentName = 256;

    public static readonly IReadOnlyList<string> KnownModels = ["inherit", "opus", "sonnet", "haiku", "fable"];

    public static IReadOnlyList<ValidationIssue> ValidateAgent(string path, string text)
    {
        var doc = FrontmatterDocument.Parse(text);
        var issues = new List<ValidationIssue>(doc.ParseIssues);
        if (!doc.HasFrontmatter)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "frontmatter(--- 블록)가 없습니다. name이 없으면 Claude Code는 이 파일을 문서로 보고 에이전트로 읽지 않습니다.", 1));
            return issues;
        }

        var name = doc.Get("name");
        var expected = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "name이 비어 있습니다. name이 없으면 Claude Code는 이 파일을 문서로 취급합니다.", doc.LineOf("name") ?? 1));
        }
        else if (name.StartsWith('-') || name.Contains(':', StringComparison.Ordinal) || name.Length > MaxAgentName)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, $"name은 '-'로 시작하거나 ':'를 포함하거나 {MaxAgentName}자를 넘을 수 없습니다.", doc.LineOf("name")));
        }
        else if (!string.Equals(name, expected, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Info, $"name '{name}'이 파일 이름 '{expected}'과 다릅니다. 에이전트 이름은 name으로 정해집니다.", doc.LineOf("name")));
        }

        if (string.IsNullOrWhiteSpace(doc.Get("description")))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "description이 비어 있습니다.", doc.LineOf("description") ?? 1));
        }

        if (doc.Get("model") is { Length: > 0 } model && !KnownModels.Contains(model, StringComparer.Ordinal)
            && !model.StartsWith("claude-", StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Info, $"알려지지 않은 model 값입니다: {model}", doc.LineOf("model")));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateSkill(string path, string text)
    {
        var doc = FrontmatterDocument.Parse(text);
        var issues = new List<ValidationIssue>(doc.ParseIssues);
        if (!doc.HasFrontmatter)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "frontmatter(--- 블록)가 없습니다. description이 없으면 Claude가 언제 이 스킬을 쓸지 판단하기 어렵습니다.", 1));
            return issues;
        }

        var name = doc.Get("name");
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Info, $"name이 없어 폴더 이름 '{folder}'을 스킬 이름으로 씁니다.", doc.LineOf("name") ?? 1));
        }
        else if (!string.Equals(name, folder, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, $"name '{name}'이 폴더 이름 '{folder}'과 다릅니다. /{name}으로 호출됩니다.", doc.LineOf("name")));
        }

        var description = doc.Get("description");
        var whenToUse = doc.Get("when_to_use") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "description이 비어 있습니다. Claude가 언제 이 스킬을 쓸지 판단하는 데 쓰입니다.", doc.LineOf("description") ?? 1));
        }
        else if (description.Length + whenToUse.Length > MaxSkillDescription)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning,
                $"description과 when_to_use를 합쳐 {MaxSkillDescription}자를 넘습니다({description.Length + whenToUse.Length}자). 목록에서 잘립니다.",
                doc.LineOf("description")));
        }

        return issues;
    }
}
