namespace DotDeck.Core.Icons;

/// <summary>Theme the icon is drawn for. Material has light variants for some icons (<c>light</c> section).</summary>
public enum IconTheme
{
    Dark,
    Light,
}

/// <summary>AI tool whose configuration folder an item belongs to.</summary>
public enum IconTool
{
    Claude,
    Codex,

    /// <summary>Antigravity CLI (agy), ~/.gemini and workspace .gemini folders.</summary>
    Antigravity,

    /// <summary>Folders read by more than one tool (.agents: Codex and agy skills/rules).</summary>
    Shared,
}

/// <summary>
/// Where an item sits inside a tool's configuration folder. Common names (agents, hooks, rules, config,
/// settings.json) get DotDeck's tool icons only with a scope, so an ordinary project's <c>config/</c> keeps its
/// normal icon.
/// </summary>
/// <param name="Tool">Tool owning the configuration root.</param>
/// <param name="RelativePath">Path of the item itself below that root, '/'-separated (e.g. "agents", "agents/a.md").</param>
public sealed record IconScope(IconTool Tool, string RelativePath);
