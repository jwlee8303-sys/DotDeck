using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace DotDeck.Core.AiCli.Guard;

public enum GuardVerdict
{
    /// <summary>No opinion: the normal permission flow decides. The guard never grants (never emits allow).</summary>
    None,

    /// <summary>Ask the user to confirm (shell commands whose targets cannot be determined, foreign MCP tools).</summary>
    Ask,

    /// <summary>Block the tool call.</summary>
    Deny,
}

public sealed record GuardDecision(GuardVerdict Verdict, string? Reason)
{
    public static GuardDecision NoDecision { get; } = new(GuardVerdict.None, null);

    public static GuardDecision Deny(string reason) => new(GuardVerdict.Deny, reason);

    public static GuardDecision Ask(string reason) => new(GuardVerdict.Ask, reason);

    /// <summary>The stricter of two decisions (deny &gt; ask &gt; none).</summary>
    public GuardDecision Max(GuardDecision other) => other.Verdict > Verdict ? other : this;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// The PreToolUse hook stdout for this decision, or null for <see cref="GuardVerdict.None"/> (exit 0 with no
    /// output leaves the decision to the normal permission flow).
    /// </summary>
    public string? ToHookOutputJson()
    {
        if (Verdict == GuardVerdict.None)
        {
            return null;
        }

        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = Verdict == GuardVerdict.Deny ? "deny" : "ask",
                ["permissionDecisionReason"] = Reason ?? "DotDeck 세션 범위 가드",
            },
        };
        return output.ToJsonString(JsonOptions);
    }
}
