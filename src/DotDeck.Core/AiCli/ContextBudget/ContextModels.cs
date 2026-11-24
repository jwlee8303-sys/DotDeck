using System.Security.Cryptography;
using System.Text;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>What a context item is (the tab groups and labels by it).</summary>
public enum ContextCategory
{
    /// <summary>Instruction files read at start (CLAUDE.md, AGENTS.md, GEMINI.md, rules, imports).</summary>
    Instructions,

    /// <summary>Skill names and descriptions listed to the model.</summary>
    Skills,

    /// <summary>Sub-agent names and descriptions listed to the model.</summary>
    Agents,

    /// <summary>Tool schemas of an MCP server.</summary>
    Mcp,
}

/// <summary>Whether an item's tokens are known.</summary>
public enum ContextItemState
{
    Measured,

    /// <summary>MCP server not measured yet (or its approval was reset because its settings changed).</summary>
    NotMeasured,

    /// <summary>Measuring failed; the detail says why.</summary>
    Failed,

    /// <summary>Remote server that needs an OAuth login: never measured.</summary>
    AuthRequired,

    /// <summary>Configured but turned off: not loaded, not counted.</summary>
    Disabled,
}

/// <param name="Path">File to open on click (instruction or skill file, or the MCP server's configuration file).</param>
/// <param name="Detail">Source label or status reason; never a secret value.</param>
/// <param name="ServerKey">For MCP rows, the key of the server (<see cref="McpServerSpec.Key"/>).</param>
public sealed record ContextItem(
    ContextCategory Category,
    string Name,
    string? Path,
    int Tokens,
    ContextItemState State = ContextItemState.Measured,
    string? Detail = null,
    string? ServerKey = null)
{
    /// <summary>The item adds to the total.</summary>
    public bool Counts => State == ContextItemState.Measured;
}

public enum McpTransport
{
    Stdio,
    Http,
    Sse,
}

/// <summary>
/// An MCP server as configured. Launch data (command, args, env, headers) is kept only in memory to be passed to the
/// measuring process or request as is: <see cref="ToString"/>, the cache and the report never contain it, only
/// <see cref="ConfigHash"/>.
/// </summary>
public sealed class McpServerSpec
{
    public required string Name { get; init; }

    /// <summary>"사용자 (~/.claude.json)", "Codex config.toml", ...</summary>
    public required string Source { get; init; }

    public required string SourcePath { get; init; }

    public McpTransport Transport { get; init; }

    public bool Disabled { get; init; }

    /// <summary>Why the server cannot be measured at all (e.g. a plugin server); null when it can.</summary>
    public string? Unsupported { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string? WorkingDirectory { get; init; }

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The configuration asks for an OAuth login (Claude <c>oauth</c>, Codex <c>auth = "oauth"</c>).</summary>
    public bool UsesOAuth { get; init; }

    /// <summary>Identity across analyses: configuration file and server name.</summary>
    public string Key => SourcePath.ToUpperInvariant() + "|" + Name;

    /// <summary>SHA-256 of everything that affects the launch (secrets included, hashed only).</summary>
    public string ConfigHash
    {
        get
        {
            var text = new StringBuilder();
            void Add(string label, string? value) => text.Append(label).Append('\u001f').Append(value ?? "\u0000").Append('\u001e');
            Add("t", Transport.ToString());
            Add("c", Command);
            foreach (var arg in Arguments)
            {
                Add("a", arg);
            }

            foreach (var (key, value) in Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Add("e:" + key, value);
            }

            Add("w", WorkingDirectory);
            Add("u", Url);
            foreach (var (key, value) in Headers.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Add("h:" + key, value);
            }

            Add("o", UsesOAuth ? "1" : "0");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    public override string ToString() => $"{Name} ({Source})";
}

/// <summary>What the collector found for one CLI and working folder (before MCP measurements are merged in).</summary>
public sealed record ContextCollection(
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<McpServerSpec> McpServers,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ContextWatchTarget> WatchDirectories);

/// <summary>A folder whose instruction, skill or configuration files are watched (<see cref="ContextWatcher"/>).</summary>
public sealed record ContextWatchTarget(string Directory, bool Recursive);

public sealed record ContextSuggestion(string Text, string? Path = null);

/// <summary>The "컨텍스트" tab's content for one session.</summary>
public sealed record ContextReport(
    CliKind Cli,
    string? WorkingDirectory,
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<ContextSuggestion> Suggestions,
    IReadOnlyList<string> Problems,
    DateTimeOffset AnalyzedAt,
    SessionLogMeasurement? Log = null)
{
    /// <summary>Sum of the files DotDeck estimated (instructions, skills, agents) and measured MCP servers.</summary>
    public int Total => Items.Where(i => i.Counts).Sum(i => i.Tokens);

    /// <summary>The log baseline when measured, else the estimated total.</summary>
    public int Baseline => Log is { State: SessionLogState.Measured, Baseline: { } measured } ? measured : Total;

    /// <summary>"시스템·MCP 등 기타": the measured baseline minus what DotDeck could attribute (null without a measurement).</summary>
    public int? Other => Log is { State: SessionLogState.Measured, Baseline: { } measured } ? measured - Total : null;

    public int UnmeasuredMcpCount => Items.Count(i => i.Category == ContextCategory.Mcp
        && i.State is ContextItemState.NotMeasured or ContextItemState.Failed or ContextItemState.AuthRequired);
}
