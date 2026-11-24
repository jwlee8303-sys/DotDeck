namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>"환경 설정 → AI 컨텍스트": thresholds of the "컨텍스트" tab's suggestions (stored as <c>ContextBudget</c>).</summary>
public sealed record ContextBudgetSettings
{
    /// <summary>An instruction file above this (estimated tokens) gets a "분리 권장" suggestion.</summary>
    public int InstructionFileTokens { get; init; } = 1500;

    /// <summary>An MCP server whose tools take at least this share (%) of the measured total gets a "끄기" suggestion.</summary>
    public int McpSharePercent { get; init; } = 30;

    /// <summary>A skill or agent description above this (estimated tokens) is listed as "긴 설명".</summary>
    public int DescriptionTokens { get; init; } = 120;

    /// <summary>A total above this (estimated tokens) is shown as a warning.</summary>
    public int TotalWarningTokens { get; init; } = 20000;

    /// <summary>AI 모니터 "컨텍스트" column: a usage percentage at or above this is orange.</summary>
    public int UsageWarningPercent { get; init; } = 70;

    /// <summary>AI 모니터 "컨텍스트" column: a usage percentage at or above this is red.</summary>
    public int UsageDangerPercent { get; init; } = 85;

    /// <summary>
    /// "고급 → MCP 직접 측정" (off by default): shows the [측정] buttons that start an MCP server to read its tool list.
    /// Off, MCP servers are listed by name only and their cost stays in "시스템·MCP 등 기타" of the log measurement.
    /// </summary>
    public bool McpDirectMeasure { get; init; }

    /// <summary>
    /// Claude model name (contains, longest match wins) → context window. Claude's log has no window size, so the usage
    /// percentage is an estimate from this table; edit it when models change. An empty list shows tokens only.
    /// </summary>
    public IReadOnlyList<ModelWindowRule>? ClaudeContextWindows { get; init; }

    /// <summary>Default of <see cref="ClaudeContextWindows"/>.</summary>
    public static IReadOnlyList<ModelWindowRule> DefaultClaudeContextWindows { get; } = [new("claude-", 200_000)];

    public IReadOnlyList<ModelWindowRule> EffectiveClaudeContextWindows => ClaudeContextWindows ?? DefaultClaudeContextWindows;

    /// <summary>Clamps hand-edited values.</summary>
    public ContextBudgetSettings Normalize() => this with
    {
        ClaudeContextWindows = ClaudeContextWindows?
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Match) && r.Tokens > 0)
            .Select(r => r with { Match = r.Match.Trim(), Tokens = Math.Min(r.Tokens, 100_000_000) })
            .ToList(),
        InstructionFileTokens = Math.Clamp(InstructionFileTokens, 100, 100_000),
        McpSharePercent = Math.Clamp(McpSharePercent, 1, 100),
        DescriptionTokens = Math.Clamp(DescriptionTokens, 10, 10_000),
        TotalWarningTokens = Math.Clamp(TotalWarningTokens, 1000, 1_000_000),
        UsageWarningPercent = Math.Clamp(UsageWarningPercent, 1, 100),
        UsageDangerPercent = Math.Clamp(UsageDangerPercent, Math.Clamp(UsageWarningPercent, 1, 100), 100),
    };
}
