namespace DotDeck.Core.Editors;

/// <summary>Which type-specific editor a file gets (SPEC §7).</summary>
public enum EditorKind
{
    Text,
    Markdown,
    Agent,
    Skill,
    ClaudeSettings,
}

public static class EditorKinds
{
    public static EditorKind Detect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var isMarkdown = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return EditorKind.Skill;
        }

        if (isMarkdown && string.Equals(parent, "agents", StringComparison.OrdinalIgnoreCase))
        {
            return EditorKind.Agent;
        }

        if ((string.Equals(name, "settings.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "settings.local.json", StringComparison.OrdinalIgnoreCase))
            && string.Equals(parent, ".claude", StringComparison.OrdinalIgnoreCase))
        {
            return EditorKind.ClaudeSettings;
        }

        return isMarkdown ? EditorKind.Markdown : EditorKind.Text;
    }
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A validation finding. <paramref name="Line"/> is 1-based, or null when it has no position.</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Message, int? Line = null);
