using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Security;

namespace DotDeck.Core.AiCli.Approvals;

/// <summary>
/// Claude Code side of the approval channel of a DotDeck session: a <c>PermissionRequest</c> hook
/// (<c>DotDeck.exe --dotdeck-approval &lt;sessionDir&gt;</c>) reports the request to the running DotDeck and waits for its
/// answer; a <c>PostToolUse</c> hook (<c>--dotdeck-approval-ran</c>) reports that a tool call ran.
/// <para>
/// Verified with Claude Code 2.1.330: the terminal prompt is shown while the hook waits and either side may answer; a
/// hook <c>allow</c> closes the prompt; rejecting in the terminal ends the hook process, allowing there does not (hence
/// PostToolUse); a hook <c>timeout</c> above the 600 s default is honored (650 s measured).
/// </para>
/// <para>
/// Fail-safe: whenever DotDeck cannot be reached, does not answer, or anything fails, the hook prints nothing and exits
/// 0, which leaves the decision to the CLI's own prompt. The hook never allows on its own.
/// </para>
/// </summary>
public static class ClaudeApprovalHook
{
    public const string RequestSwitch = "--dotdeck-approval";
    public const string ToolRanSwitch = "--dotdeck-approval-ran";

    /// <summary>
    /// Hook timeout written to the session settings: DotDeck's wait limit (<see cref="ApprovalSettings.HookWaitLimit"/>,
    /// 30 min) + 30 s. Claude Code's default (600 s) is shorter, so it is always written. Measured with 2.1.330: a
    /// timeout above the default is honored (a hook answered after 650 s was applied).
    /// </summary>
    public const int HookTimeoutSeconds = 30 * 60 + 30;

    public const int ToolRanTimeoutSeconds = 10;

    /// <summary>The hook process gives up a little before Claude Code would cancel it.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(HookTimeoutSeconds - 20);

    /// <summary>Connecting to DotDeck takes at most this long; otherwise the CLI prompt decides.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Present in the session folder while DotDeck has open requests of the session; the PostToolUse hook connects only
    /// then, so ordinary tool calls cost no connection.
    /// </summary>
    public const string PendingMarkerFile = "approval-pending";

    public const string KeyFile = "approval.key";

    /// <summary>Longest command text kept for display (the full input still decides the match).</summary>
    public const int MaxCommandLength = 8000;

    public const string BehaviorAllow = "allow";
    public const string BehaviorDeny = "deny";
    public const string BehaviorNone = "none";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>The request message sent to DotDeck from the hook input, or null when the input cannot be read.</summary>
    public static string? BuildRequestMessage(string hookInput)
    {
        if (Parse(hookInput) is not { } input || Text(input["tool_name"]) is not { Length: > 0 } tool)
        {
            return null;
        }

        var message = new JsonObject
        {
            ["t"] = "request",
            ["tool"] = tool,
            ["command"] = Summarize(tool, input["tool_input"]),
            ["cwd"] = Text(input["cwd"]),
            ["match"] = MatchKey(tool, input["tool_input"]),
        };
        return message.ToJsonString(JsonOptions);
    }

    /// <summary>The "tool ran" message from PostToolUse input, or null.</summary>
    public static string? BuildToolRanMessage(string hookInput)
    {
        if (Parse(hookInput) is not { } input || Text(input["tool_name"]) is not { Length: > 0 } tool)
        {
            return null;
        }

        return new JsonObject { ["t"] = "ran", ["match"] = MatchKey(tool, input["tool_input"]) }.ToJsonString(JsonOptions);
    }

    /// <summary>The answer line DotDeck sends back.</summary>
    public static string BuildDecisionMessage(string behavior) =>
        new JsonObject { ["t"] = "decision", ["behavior"] = behavior }.ToJsonString(JsonOptions);

    /// <summary>The behavior of an answer line; anything unexpected is "none".</summary>
    public static string ReadDecision(string? line)
    {
        if (line is null || Parse(line) is not { } message || Text(message["t"]) != "decision")
        {
            return BehaviorNone;
        }

        return Text(message["behavior"]) is BehaviorAllow or BehaviorDeny ? Text(message["behavior"])! : BehaviorNone;
    }

    /// <summary>The hook stdout for a behavior; null (print nothing) for "none" and anything else.</summary>
    public static string? HookOutput(string behavior)
    {
        if (behavior is not (BehaviorAllow or BehaviorDeny))
        {
            return null;
        }

        var decision = new JsonObject { ["behavior"] = behavior };
        if (behavior == BehaviorDeny)
        {
            decision["message"] = "DotDeck에서 사용자가 거부했습니다";
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision,
            },
        }.ToJsonString(JsonOptions);
    }

    /// <summary>Tool name + canonical input JSON: identifies the same call in PermissionRequest and PostToolUse.</summary>
    public static string MatchKey(string tool, JsonNode? toolInput) => tool + "\n" + (toolInput?.ToJsonString() ?? string.Empty);

    /// <summary>What the user is asked to allow: the shell command, the file path, the URL, or the input JSON (masked).</summary>
    public static string Summarize(string tool, JsonNode? toolInput)
    {
        string? text = null;
        if (toolInput is JsonObject input)
        {
            text = tool switch
            {
                "Bash" or "PowerShell" => Text(input["command"]),
                "Write" or "Edit" or "MultiEdit" or "Read" => Text(input["file_path"]),
                "NotebookEdit" => Text(input["notebook_path"]) ?? Text(input["file_path"]),
                "WebFetch" => Text(input["url"]),
                "WebSearch" => Text(input["query"]),
                _ => null,
            };
        }

        text ??= toolInput?.ToJsonString(JsonOptions) ?? string.Empty;
        text = SecretMasker.MaskText(text);
        return text.Length > MaxCommandLength ? text[..MaxCommandLength] + "…" : text;
    }

    /// <summary>A request message received by DotDeck.</summary>
    public sealed record RequestMessage(string Tool, string Command, string? WorkingDirectory, string Match);

    /// <summary>Reads a request message (DotDeck side); null for anything else.</summary>
    public static RequestMessage? ReadRequest(string? line)
    {
        if (line is null || Parse(line) is not { } message || Text(message["t"]) != "request"
            || Text(message["tool"]) is not { Length: > 0 } tool || Text(message["match"]) is not { } match)
        {
            return null;
        }

        var cwd = Text(message["cwd"]);
        return new RequestMessage(
            tool,
            Text(message["command"]) ?? string.Empty,
            cwd is not null && Path.IsPathFullyQualified(cwd) ? cwd : null,
            match);
    }

    /// <summary>Reads a "tool ran" message (DotDeck side): its match key, or null.</summary>
    public static string? ReadToolRan(string? line) =>
        line is not null && Parse(line) is { } message && Text(message["t"]) == "ran" ? Text(message["match"]) : null;

    /// <summary>Creates the approval request DotDeck shows for a hook message.</summary>
    public static ApprovalRequest ToRequest(RequestMessage message, string sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ApprovalRequest(sessionId, CliKind.Claude, ApprovalSource.Hook, message.Tool, message.Command, message.WorkingDirectory, now)
        {
            CanRespond = true,
            MatchKey = message.Match,
        };
    }

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return JsonNode.Parse(json ?? string.Empty) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>Claude Code sessions started by DotDeck: PermissionRequest hook channel.</summary>
public sealed class ClaudeHookApprovalProvider : IApprovalProvider
{
    public CliKind Cli => CliKind.Claude;

    public ApprovalSource Source => ApprovalSource.Hook;

}
