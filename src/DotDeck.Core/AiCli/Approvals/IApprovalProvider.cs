using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>
/// How DotDeck learns about approval prompts of one CLI and how it answers them. CLI differences (detection channel,
/// screen patterns, answer keys) live behind this interface: another CLI is one more implementation.
/// </summary>
public interface IApprovalProvider
{
    CliKind Cli { get; }

    ApprovalSource Source { get; }
}

/// <summary>The way back to the CLI for one request (a hook connection, or key presses into a terminal).</summary>
public interface IApprovalChannel
{
    /// <summary>
    /// Re-checks that the request is still open on the CLI side, then delivers the decision. False when it could not
    /// be delivered (already answered, prompt gone, connection closed): nothing was sent.
    /// </summary>
    Task<bool> SendAsync(ApprovalDecision decision, CancellationToken cancellationToken);

    /// <summary>
    /// Stops waiting without a decision: the CLI's own prompt decides (hook: ends with no output). Idempotent; does
    /// nothing for screen requests.
    /// </summary>
    void Release();
}

/// <summary>Writes the record of answered requests (who, when, what).</summary>
public interface IApprovalLog
{
    void Record(ApprovalRequest request, ApprovalResponder responder);
}
