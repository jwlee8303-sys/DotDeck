namespace DotDeck.Core.Editors.Registry;

/// <summary>One source of editor names, labels and validation. Unimplemented editors stay out of format menus.</summary>
public static class EditorRegistry
{
    public static IReadOnlyList<EditorDescriptor> All { get; } = Array.AsReadOnly<EditorDescriptor>(
    [
        new(EditorKind.Text, "text", "텍스트"),
        new(EditorKind.Markdown, "markdown", "Markdown", HasOutline: true),
        new(EditorKind.ClaudeSettings, "settingsForm", "설정 폼", "claude", HasForm: true,
            Validate: ValidateSettings, FormError: SettingsError),
        new(EditorKind.Agent, "agentForm", "에이전트 폼", "claude", HasForm: true, HasOutline: true,
            Validate: c => MarkdownAgentValidator.ValidateAgent(c.Path, c.Text), FormError: FrontmatterError),
        new(EditorKind.Skill, "skillForm", "스킬 폼", "claude", HasForm: true, HasOutline: true,
            Validate: c => MarkdownAgentValidator.ValidateSkill(c.Path, c.Text), FormError: FrontmatterError),
        new(EditorKind.CodexConfig, "codexConfigForm", "Codex 설정 폼", "codex"),
        new(EditorKind.CodexHooks, "codexHooksForm", "Codex 훅 폼", "codex"),
        new(EditorKind.CodexAgent, "codexAgentForm", "Codex 에이전트 폼", "codex"),
        new(EditorKind.CodexSkillMetadata, "openaiYamlForm", "Codex 스킬 메타데이터 폼", "codex"),
        new(EditorKind.CodexRules, "codexRulesForm", "Codex 규칙 폼", "codex"),
    ]);

    private static readonly IReadOnlyDictionary<EditorKind, EditorDescriptor> s_byKind = All.ToDictionary(d => d.Kind);
    private static readonly IReadOnlyDictionary<string, EditorDescriptor> s_byName = All.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static EditorDescriptor Get(EditorKind kind) => s_byKind.TryGetValue(kind, out var descriptor) ? descriptor : s_byKind[EditorKind.Text];

    public static EditorKind? FindKind(string? name) => name is not null && s_byName.TryGetValue(name, out var descriptor) ? descriptor.Kind : null;

    private static string? FrontmatterError(string text) =>
        FrontmatterDocument.Parse(text).ParseIssues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;

    private static string? SettingsError(string text)
    {
        _ = ClaudeSettingsDocument.TryParse(text, out var error);
        return error;
    }

    private static IReadOnlyList<ValidationIssue> ValidateSettings(EditorValidationContext context) =>
        ClaudeSettingsDocument.TryParse(context.Text, out var error) is { } settings
            ? settings.Validate(context.Store.FileExists, context.Settings, context.Keys, context.Tools, context.Events)
            : [new ValidationIssue(IssueSeverity.Error, error ?? "JSON 오류")];
}
