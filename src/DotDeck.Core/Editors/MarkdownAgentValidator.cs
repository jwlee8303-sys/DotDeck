namespace DotDeck.Core.Editors;

/// <summary>Checks of agents/*.md and skills/&lt;name&gt;/SKILL.md (SPEC §7).</summary>
public static class MarkdownAgentValidator
{
    public const int MaxSkillDescription = 1024;

    public static readonly IReadOnlyList<string> KnownModels = ["inherit", "opus", "sonnet", "haiku"];

    public static IReadOnlyList<ValidationIssue> ValidateAgent(string path, string text)
    {
        var doc = FrontmatterDocument.Parse(text);
        var issues = new List<ValidationIssue>(doc.ParseIssues);
        if (!doc.HasFrontmatter)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "frontmatter(--- 블록)가 없습니다. name·description이 필요합니다.", 1));
            return issues;
        }

        var name = doc.Get("name");
        var expected = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "name이 비어 있습니다.", doc.LineOf("name") ?? 1));
        }
        else if (!string.Equals(name, expected, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, $"name '{name}'이 파일 이름 '{expected}'과 다릅니다.", doc.LineOf("name")));
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
            issues.Add(new ValidationIssue(IssueSeverity.Error, "frontmatter(--- 블록)가 없습니다. name·description이 필요합니다.", 1));
            return issues;
        }

        var name = doc.Get("name");
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "name이 비어 있습니다.", doc.LineOf("name") ?? 1));
        }
        else if (!string.Equals(name, folder, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, $"name '{name}'이 폴더 이름 '{folder}'과 같아야 합니다.", doc.LineOf("name")));
        }

        var description = doc.Get("description");
        if (string.IsNullOrWhiteSpace(description))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, "description이 비어 있습니다.", doc.LineOf("description") ?? 1));
        }
        else if (description.Length > MaxSkillDescription)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, $"description이 {MaxSkillDescription}자를 넘습니다({description.Length}자).", doc.LineOf("description")));
        }

        return issues;
    }
}
