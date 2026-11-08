using System.Text.Json.Nodes;

namespace DotDeck.Core.Hooks;

/// <summary>One flattened hook command: <c>hooks.&lt;Event&gt;[i].hooks[j]</c>.</summary>
/// <param name="CommandExtra">The original command object (unknown fields are written back).</param>
/// <param name="GroupExtra">The original matcher group object (unknown fields are written back).</param>
/// <param name="GroupKey">Original group ("Event#index"); rows of one group stay in one group when written back.</param>
public sealed record HookHandlerModel(
    string Event,
    string Matcher,
    string Command,
    int? Timeout = null,
    JsonObject? CommandExtra = null,
    JsonObject? GroupExtra = null,
    string? GroupKey = null)
{
    /// <summary>Handler type (<c>command</c>, <c>http</c>, <c>prompt</c>, <c>agent</c>, <c>mcp_tool</c>); missing means command.</summary>
    public string Type => CommandExtra?["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "command";

    /// <summary>Exec-form arguments (<c>args</c>), when present.</summary>
    public IReadOnlyList<string>? Args => CommandExtra?["args"] is JsonArray a
        ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty).ToList()
        : null;
}
