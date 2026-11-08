namespace DotDeck.Core.Editors;

/// <summary>Which type-specific editor a file gets (SPEC §7). Chosen by <see cref="Roles.FileRoleResolver"/> or the user.</summary>
public enum EditorKind
{
    Text,
    Markdown,
    Agent,
    Skill,
    ClaudeSettings,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A validation finding. <paramref name="Line"/> is 1-based, or null when it has no position.</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Message, int? Line = null);
