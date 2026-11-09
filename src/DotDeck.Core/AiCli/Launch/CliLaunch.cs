using DotDeck.Core.AiCli.Scope;

namespace DotDeck.Core.AiCli.Launch;

public enum CliKind
{
    Claude,
    Codex,
}

public enum LaunchMode
{
    /// <summary>Interactive TUI in a ConPTY terminal tab (SPEC 11.5 A).</summary>
    Interactive,

    /// <summary>One prompt, streamed output in the AI 작업 panel (SPEC 11.5 B).</summary>
    NonInteractive,
}

/// <summary>Where a CLI was found: the program to start plus arguments that must precede the CLI's own (node + script).</summary>
public sealed record CliLocation(CliKind Cli, string Executable, IReadOnlyList<string> PrefixArguments)
{
    public string DisplayName => Cli == CliKind.Claude ? "Claude" : "Codex";
}

/// <summary>Everything needed to build one AI CLI launch.</summary>
public sealed record CliLaunchRequest
{
    public required CliLocation Cli { get; init; }

    public required LaunchMode Mode { get; init; }

    public required SessionScope Scope { get; init; }

    /// <summary>Session folder (<c>%LOCALAPPDATA%\DotDeck\sessions\&lt;id&gt;</c>) holding settings.json, scope.json, ….</summary>
    public required string SessionDirectory { get; init; }

    /// <summary>Context document passed as additional system prompt / developer instructions.</summary>
    public string? Context { get; init; }

    /// <summary>Conversation id: a new id for <c>--session-id</c>, or the id to resume.</summary>
    public string? ConversationId { get; init; }

    /// <summary>True to resume <see cref="ConversationId"/> instead of starting it.</summary>
    public bool Resume { get; init; }

    /// <summary>Prompt for non-interactive runs.</summary>
    public string? Prompt { get; init; }

    /// <summary>Non-interactive option "읽기 전용 셸 허용" (A14, default off).</summary>
    public bool AllowReadOnlyShell { get; init; }

    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Session MCP config (5c); null until DotDeck's MCP server exists.</summary>
    public string? McpConfigPath { get; init; }

    public string? McpBridgeExecutable { get; init; }

    public string? WindowsSandboxMode { get; init; }

    /// <summary>
    /// Sandbox homes (A4): set only when DotDeck runs with <c>--home</c>/<c>DOTDECK_HOME</c>; null in real-home mode
    /// so the user's own CLI configuration is used.
    /// </summary>
    public string? SandboxClaudeConfigDir { get; init; }

    public string? SandboxCodexHome { get; init; }
}

/// <summary>A fully built launch: what to start, where, with which environment additions.</summary>
public sealed record CliLaunchPlan
{
    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Variables added to (or overriding) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Text written to stdin (Codex non-interactive prompt), or null.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Indexes of free-text arguments (prompt, context) that the preview folds and the option check skips.</summary>
    public IReadOnlySet<int> FreeTextArguments { get; init; } = new HashSet<int>();

    public string CommandLine => WindowsCommandLine.Join(Executable, Arguments);
}
