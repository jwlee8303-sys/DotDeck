using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>Where an approval request was seen.</summary>
public enum ApprovalSource
{
    /// <summary>A hook of a DotDeck session reported it (official channel; the answer goes back through the hook).</summary>
    Hook,

    /// <summary>A prompt pattern on the terminal screen plus stopped output (an estimate; answered with key presses).</summary>
    Screen,
}

/// <summary>Life of one approval request.</summary>
public enum ApprovalState
{
    /// <summary>Waiting for an answer.</summary>
    Pending,

    /// <summary>An answer is being delivered (the request is re-checked first); no second answer is accepted.</summary>
    Responding,

    /// <summary>Allowed from DotDeck (AI 모니터 or notification card).</summary>
    Allowed,

    /// <summary>Denied from DotDeck.</summary>
    Denied,

    /// <summary>DotDeck stopped waiting; the CLI's own prompt in the terminal decides ("터미널에서 응답 중").</summary>
    HandedToTerminal,

    /// <summary>Answered in the terminal (seen through the hook ending or the tool running, or the prompt leaving the screen).</summary>
    AnsweredInTerminal,

    /// <summary>No longer answerable from DotDeck (wait limit reached, session or prompt gone).</summary>
    Expired,
}

/// <summary>The one-time answer DotDeck sends. There is no "always allow".</summary>
public enum ApprovalDecision
{
    Allow,
    Deny,
}

/// <summary>Who answered (session record).</summary>
public enum ApprovalResponder
{
    User,
    Terminal,
}

/// <summary>
/// One approval request of a CLI. Created by a provider, owned by <see cref="ApprovalService"/>: every state change goes
/// through the service (under its lock), the UI only reads.
/// </summary>
public sealed class ApprovalRequest
{
    public ApprovalRequest(string sessionKey, CliKind cli, ApprovalSource source, string toolName, string command, string? workingDirectory, DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        Id = Guid.NewGuid().ToString("N");
        SessionKey = sessionKey;
        Cli = cli;
        Source = source;
        ToolName = toolName ?? string.Empty;
        Command = command ?? string.Empty;
        WorkingDirectory = workingDirectory;
        RequestedAt = requestedAt;
    }

    /// <summary>Unique ID; answers apply only to the request with this ID.</summary>
    public string Id { get; }

    /// <summary>The DotDeck session ID, or the terminal row key of a CLI run by hand.</summary>
    public string SessionKey { get; }

    public CliKind Cli { get; }

    public ApprovalSource Source { get; }

    /// <summary>Tool name as the CLI reports it (Bash, Write, …) or a description for screen prompts.</summary>
    public string ToolName { get; }

    /// <summary>Full command or tool input (secrets masked).</summary>
    public string Command { get; }

    public string? WorkingDirectory { get; }

    public DateTimeOffset RequestedAt { get; }

    /// <summary>DotDeck can answer (hook channel, or a verified screen prompt of the installed CLI version).</summary>
    public bool CanRespond { get; init; }

    /// <summary>Why DotDeck cannot answer, shown instead of the buttons ("터미널로 이동"만).</summary>
    public string? CannotRespondReason { get; init; }

    /// <summary>Matching tool call of a later PostToolUse (hook requests): tool name + input JSON.</summary>
    public string? MatchKey { get; init; }

    public ApprovalState State { get; internal set; } = ApprovalState.Pending;

    /// <summary>When DotDeck stops waiting and hands the request to the terminal prompt.</summary>
    public DateTimeOffset? WaitLimit { get; internal set; }

    public DateTimeOffset? ResolvedAt { get; internal set; }

    /// <summary>Short result text for a finished request ("허용됨", "허용됨(터미널)", …).</summary>
    public string? ResultText { get; internal set; }

    public bool IsWaiting => State is ApprovalState.Pending or ApprovalState.Responding;

    public bool IsFinished => !IsWaiting && State != ApprovalState.HandedToTerminal;
}
