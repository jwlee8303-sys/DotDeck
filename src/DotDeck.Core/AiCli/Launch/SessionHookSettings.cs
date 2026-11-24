using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotDeck.Core.AiCli.Launch;

/// <summary>
/// "AI 세션 훅": which DotDeck hooks go into the settings.json of a new Claude session and the user's extra hooks
/// for DotDeck sessions only. Applies to sessions started afterwards; running sessions keep the file they started with.
/// </summary>
public sealed record SessionHookSettings
{
    /// <summary>PreToolUse tool guard (project scope, secret files). On by default.</summary>
    public bool Guard { get; init; } = true;

    /// <summary>PermissionRequest/PostToolUse approval hooks of interactive sessions. On by default.</summary>
    public bool Approval { get; init; } = true;

    /// <summary>
    /// Extra hooks in Claude Code's <c>hooks</c> format (<c>{"PreToolUse":[{"matcher":"…","hooks":[…]}]}</c>); null
    /// means none. Merged after DotDeck's own groups, never replacing them.
    /// </summary>
    public JsonObject? UserHooks { get; init; }

    [JsonIgnore]
    public bool HasUserHooks => UserHooks is { Count: > 0 };

    public SessionHookSettings Normalize() => this with
    {
        UserHooks = UserHooks is { Count: > 0 } ? UserHooks : null,
    };
}

/// <summary>What <see cref="SessionFilesWriter"/> puts into one session's settings.json.</summary>
/// <param name="Guard">Adds the PreToolUse guard hook.</param>
/// <param name="Approval">Adds the approval hooks (interactive sessions with a running approval host only).</param>
/// <param name="UserHooks">Extra hook groups appended per event.</param>
public sealed record SessionHookPlan(bool Guard, bool Approval, JsonObject? UserHooks)
{
    /// <summary>DotDeck's guard and heartbeat only (non-interactive and older callers).</summary>
    public static SessionHookPlan GuardOnly { get; } = new(true, false, null);
}
