namespace DotDeck.Core.Editors;

/// <summary>Product-specific fields, suggestions and validation for the shared frontmatter editor.</summary>
public sealed record FrontmatterProfile(
    string Product,
    EditorKind Kind,
    bool ShowAgentFields,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Tools,
    Func<string, string, IReadOnlyList<ValidationIssue>> Validate);

public static class FrontmatterProfiles
{
    private static readonly IReadOnlyList<string> s_tools = Array.AsReadOnly<string>(
        ["Read", "Write", "Edit", "Glob", "Grep", "Bash", "PowerShell", "WebFetch", "WebSearch", "Agent", "Skill", "NotebookEdit"]);

    public static FrontmatterProfile ClaudeAgent { get; } = new("claude", EditorKind.Agent, true,
        MarkdownAgentValidator.KnownModels, s_tools, MarkdownAgentValidator.ValidateAgent);

    public static FrontmatterProfile ClaudeSkill { get; } = new("claude", EditorKind.Skill, false,
        MarkdownAgentValidator.KnownModels, s_tools, MarkdownAgentValidator.ValidateSkill);

    public static FrontmatterProfile For(EditorKind kind) => kind switch
    {
        EditorKind.Agent => ClaudeAgent,
        EditorKind.Skill => ClaudeSkill,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a frontmatter editor."),
    };
}
