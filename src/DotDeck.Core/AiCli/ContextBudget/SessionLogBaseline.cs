using System.Globalization;
using System.Text;
using System.Text.Json;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.ContextBudget;

public enum SessionLogState
{
    /// <summary>Baseline and current input read from the log.</summary>
    Measured,

    /// <summary>The log has no model answer yet ("첫 응답 후 측정").</summary>
    WaitingFirstAnswer,

    /// <summary>No single log file belongs to the session for sure ("매칭 불가").</summary>
    NotMatched,

    /// <summary>The file was found but its records could not be read ("측정 불가").</summary>
    Unreadable,

    /// <summary>The CLI has no supported log (agy).</summary>
    Unsupported,

    /// <summary>The source needs a connection that is not installed (agy status line: "agy 연동 필요").</summary>
    NotConnected,
}

/// <summary>
/// What a session's own log says about its context. <paramref name="Baseline"/> is the first request's input minus the
/// estimated tokens of the first user message; <paramref name="CurrentInput"/> is the latest request's input.
/// <paramref name="WindowIsEstimated"/> is true when the window comes from the model name table (Claude).
/// <paramref name="LastWrite"/> is the log file's last write (compared with status line values).
/// </summary>
public sealed record SessionLogMeasurement(
    SessionLogState State,
    string? LogPath = null,
    int? Baseline = null,
    int? FirstRequestInput = null,
    int? FirstPromptTokens = null,
    int? CurrentInput = null,
    int? ContextWindow = null,
    bool WindowIsEstimated = false,
    string? Model = null,
    DateTimeOffset? LastWrite = null)
{
    public double? UsedPercent => CurrentInput is { } input && ContextWindow is > 0 and { } window ? input * 100.0 / window : null;
}

/// <summary>One "model name contains" → context window rule of <see cref="ContextBudgetSettings.ClaudeContextWindows"/>.</summary>
public sealed record ModelWindowRule(string Match, int Tokens);

/// <summary>
/// Which session to look for: CLI, working folder, start time, the session id when known (DotDeck Claude sessions,
/// Claude's status line) and the log path when the status line reported it. <paramref name="ClaimedLogs"/> are log
/// files other sessions already use (never matched by folder again). <paramref name="FirstBefore"/>: a later session
/// in the same folder is still unmatched, so only files first written before it started can be this one's.
/// </summary>
public sealed record SessionLogQuery(
    CliKind Cli,
    string? WorkingDirectory,
    DateTimeOffset StartedAt,
    string? ConversationId = null,
    string? LogPath = null,
    IReadOnlySet<string>? ClaimedLogs = null,
    DateTimeOffset? FirstBefore = null);

/// <summary>A matched log file, or why there is none.</summary>
public sealed record SessionLogMatch(SessionLogState State, string? Path = null);

/// <summary>
/// Reads a session's context from the CLI's local log. The formats are not documented (checked on 2026-11-24): any
/// unexpected record makes the result <see cref="SessionLogState.Unreadable"/>, never an exception.
/// Files are read incrementally: each file keeps its read position and parser state, so a growing log is read from
/// where the last read stopped. A file that shrank or was replaced starts over; a file that is gone must be matched
/// again (<see cref="Match"/>).
/// <list type="bullet">
/// <item>Codex <c>$CODEX_HOME/sessions/YYYY/MM/DD/rollout-*.jsonl</c>: <c>session_meta</c> (cwd, timestamp), the first
/// <c>role=user</c> message after <c>turn_context</c> (the earlier user message is Codex's own AGENTS.md injection), and
/// <c>event_msg</c> <c>token_count</c> <c>info.last_token_usage.input_tokens</c> / <c>info.model_context_window</c>.</item>
/// <item>Claude <c>~/.claude/projects/&lt;folder&gt;/&lt;session id&gt;.jsonl</c>: the first main-thread user text (not
/// <c>tool_result</c>, not meta) and assistant <c>message.usage</c> input + cache creation + cache read, one per
/// <c>message.id</c>, sub-agent (<c>isSidechain</c>) records left out. The log has no window size: it comes from the
/// model name table and is marked estimated.</item>
/// </list>
/// </summary>
public sealed class SessionLogReader(string claudeHome, string codexHome)
{
    /// <summary>
    /// A log found by folder (no session id known) must start within this time after the session start: the CLIs may
    /// write their log only from the first prompt. Two candidates make the match uncertain ("매칭 불가").
    /// </summary>
    public static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan s_earlyTolerance = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LogTail> _tails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Matches the session's log and reads what is new in it.</summary>
    public SessionLogMeasurement Read(SessionLogQuery query, IReadOnlyList<ModelWindowRule> windows)
    {
        ArgumentNullException.ThrowIfNull(query);
        var match = Match(query);
        return match is { State: SessionLogState.Measured, Path: { } path }
            ? ReadFile(query.Cli, path, windows)
            : new SessionLogMeasurement(match.State);
    }

    /// <summary>Finds the session's log file (State Measured with a path), or the reason there is none.</summary>
    public SessionLogMatch Match(SessionLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            if (query.LogPath is { } known && Path.IsPathFullyQualified(known) && File.Exists(known)
                && (query.Cli != CliKind.Claude || query.ConversationId is null
                    || string.Equals(Path.GetFileNameWithoutExtension(known), query.ConversationId, StringComparison.OrdinalIgnoreCase)))
            {
                return new SessionLogMatch(SessionLogState.Measured, known);
            }

            return query.Cli switch
            {
                CliKind.Codex => MatchCodex(query),
                CliKind.Claude => MatchClaude(query),
                _ => new SessionLogMatch(SessionLogState.Unsupported),
            };
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return new SessionLogMatch(SessionLogState.Unreadable);
        }
    }

    /// <summary>
    /// Reads the lines appended to <paramref name="path"/> since the last read. A missing file gives
    /// <see cref="SessionLogState.NotMatched"/> (match again); a file that shrank or was replaced is read from the start.
    /// </summary>
    public SessionLogMeasurement ReadFile(CliKind cli, string path, IReadOnlyList<ModelWindowRule> windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(windows);
        LogTail tail;
        lock (_gate)
        {
            if (!_tails.TryGetValue(path, out tail!))
            {
                tail = new LogTail(path, cli == CliKind.Codex ? new CodexParser() : new ClaudeParser());
                _tails[path] = tail;
            }
        }

        SessionLogMeasurement result;
        try
        {
            result = tail.Read();
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            result = new SessionLogMeasurement(SessionLogState.Unreadable, path);
        }

        if (result.State == SessionLogState.NotMatched)
        {
            lock (_gate)
            {
                _tails.Remove(path);
            }
        }

        if (cli != CliKind.Claude || result.State != SessionLogState.Measured)
        {
            return result;
        }

        var window = WindowFor(result.Model, windows);
        if (window is not null && result.CurrentInput > window)
        {
            window = null; // the table is wrong for this model: show tokens only
        }

        return result with { ContextWindow = window, WindowIsEstimated = window is not null };
    }

    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException;

    // ---------------------------------------------------------------- matching

    private SessionLogMatch MatchCodex(SessionLogQuery query)
    {
        var root = Path.Combine(codexHome, "sessions");
        if (query.WorkingDirectory is null && query.ConversationId is null)
        {
            return new SessionLogMatch(SessionLogState.NotMatched);
        }

        if (!Directory.Exists(root))
        {
            return new SessionLogMatch(SessionLogState.WaitingFirstAnswer); // no log written yet
        }

        var candidates = new List<string>();
        var byId = new List<string>(); // the id DotDeck knows is not always Codex's thread id: folder and time are the fallback
        var days = new HashSet<DateOnly>();
        foreach (var t in new[] { query.StartedAt.ToLocalTime(), query.StartedAt.ToUniversalTime() })
        {
            days.Add(DateOnly.FromDateTime(t.DateTime.AddDays(-1)));
            days.Add(DateOnly.FromDateTime(t.DateTime));
            days.Add(DateOnly.FromDateTime(t.DateTime.AddDays(1)));
        }

        foreach (var day in days)
        {
            var dir = Path.Combine(root, day.Year.ToString("D4", CultureInfo.InvariantCulture), day.Month.ToString("D2", CultureInfo.InvariantCulture),
                day.Day.ToString("D2", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
            {
                if (query.ConversationId is { Length: > 0 } thread && file.EndsWith("-" + thread + ".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    byId.Add(file);
                    continue;
                }

                if (query.WorkingDirectory is null || File.GetLastWriteTimeUtc(file) < query.StartedAt.UtcDateTime - s_earlyTolerance)
                {
                    continue;
                }

                if (CodexMeta(file) is { } meta && SamePath(meta.Cwd, query.WorkingDirectory)
                    && meta.Started >= query.StartedAt - s_earlyTolerance && meta.Started <= query.StartedAt + MatchWindow)
                {
                    candidates.Add(file);
                }
            }
        }

        if (byId.Count == 1)
        {
            return new SessionLogMatch(SessionLogState.Measured, byId[0]);
        }

        return candidates.Count switch
        {
            1 => new SessionLogMatch(SessionLogState.Measured, candidates[0]),
            0 => new SessionLogMatch(SessionLogState.WaitingFirstAnswer),
            _ => new SessionLogMatch(SessionLogState.NotMatched),
        };
    }

    private static (string Cwd, DateTimeOffset Started)? CodexMeta(string file)
    {
        foreach (var line in ReadLines(file).Take(5))
        {
            using var doc = TryParse(line);
            if (doc is null || Str(doc.RootElement, "type") != "session_meta"
                || !doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var started = Str(payload, "timestamp") ?? Str(doc.RootElement, "timestamp");
            if (Str(payload, "cwd") is { } cwd && started is not null
                && DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return (cwd, at);
            }
        }

        return null;
    }

    private SessionLogMatch MatchClaude(SessionLogQuery query)
    {
        var projects = Path.Combine(claudeHome, "projects");
        if (query.WorkingDirectory is null && query.ConversationId is null)
        {
            return new SessionLogMatch(SessionLogState.NotMatched);
        }

        if (!Directory.Exists(projects))
        {
            return new SessionLogMatch(SessionLogState.WaitingFirstAnswer); // no log written yet
        }

        if (query.ConversationId is { } id)
        {
            if (!Guid.TryParse(id, out var conversation))
            {
                return new SessionLogMatch(SessionLogState.NotMatched);
            }

            // The session is known: its own transcript or nothing yet (never another one of the folder).
            return Directory.EnumerateFiles(projects, conversation.ToString("D") + ".jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
                .FirstOrDefault() is { } byId
                ? new SessionLogMatch(SessionLogState.Measured, byId)
                : new SessionLogMatch(SessionLogState.WaitingFirstAnswer);
        }

        if (query.WorkingDirectory is not { } cwd)
        {
            return new SessionLogMatch(SessionLogState.WaitingFirstAnswer);
        }

        var dir = Path.Combine(projects, ClaudeProjectFolder(cwd));
        if (!Directory.Exists(dir))
        {
            return new SessionLogMatch(SessionLogState.WaitingFirstAnswer);
        }

        // Claude Code writes the transcript only with the first message, which may come long after the start: no upper
        // bound. Of the transcripts first written after the start that no other session uses, the earliest is this one.
        string? best = null;
        var bestFirst = DateTimeOffset.MaxValue;
        foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(path) < query.StartedAt.UtcDateTime - s_earlyTolerance
                || query.ClaimedLogs?.Any(claimed => SamePath(claimed, path)) == true)
            {
                continue;
            }

            if (ClaudeFirstTimestamp(path) is { } first && first >= query.StartedAt - s_earlyTolerance && first < bestFirst
                && (query.FirstBefore is not { } before || first < before))
            {
                best = path;
                bestFirst = first;
            }
        }

        return best is not null
            ? new SessionLogMatch(SessionLogState.Measured, best)
            : new SessionLogMatch(SessionLogState.WaitingFirstAnswer);
    }

    /// <summary>Claude Code's folder name for a working directory: every character other than a letter or digit becomes '-'.</summary>
    public static string ClaudeProjectFolder(string cwd)
    {
        var name = new StringBuilder(cwd.Length);
        foreach (var c in Path.TrimEndingDirectorySeparator(cwd))
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        return name.ToString();
    }

    public static int? WindowFor(string? model, IReadOnlyList<ModelWindowRule> windows)
    {
        if (string.IsNullOrEmpty(model) || windows is null)
        {
            return null;
        }

        // The longest matching rule wins ("claude-opus-5" before "claude-").
        return windows
            .Where(w => w.Match.Length > 0 && w.Tokens > 0 && model.Contains(w.Match, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Match.Length)
            .Select(w => (int?)w.Tokens)
            .FirstOrDefault();
    }

    private static DateTimeOffset? ClaudeFirstTimestamp(string file)
    {
        foreach (var line in ReadLines(file).Take(50))
        {
            if (!line.Contains("\"timestamp\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var doc = TryParse(line);
            if (doc is not null && Str(doc.RootElement, "timestamp") is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- incremental reading

    /// <summary>A log file read from where the previous read stopped (whole lines only).</summary>
    private sealed class LogTail(string path, ILogParser parser)
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _partial = new();
        private ILogParser _parser = parser;
        private long _position;
        private DateTime _created;

        public SessionLogMeasurement Read()
        {
            lock (_gate)
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return new SessionLogMeasurement(SessionLogState.NotMatched, path);
                }

                // Shrunk or replaced (a new file has a new creation time): start over.
                if (info.Length < _position || (_position > 0 && info.CreationTimeUtc != _created))
                {
                    _position = 0;
                    _partial.SetLength(0);
                    _parser = _parser.Fresh();
                }

                _created = info.CreationTimeUtc;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(_position, SeekOrigin.Begin);
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        _position += read;
                        var start = 0;
                        for (var i = 0; i < read; i++)
                        {
                            if (buffer[i] != (byte)'\n')
                            {
                                continue;
                            }

                            _partial.Write(buffer, start, i - start);
                            Feed();
                            start = i + 1;
                        }

                        _partial.Write(buffer, start, read - start);
                    }
                }

                // A last line without its newline is kept for the next read (it may still be written), but read now
                // when it is already a whole JSON object: the parsers give the same result for a line fed twice.
                if (_partial.Length > 0)
                {
                    var last = Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length).Trim();
                    if (last.StartsWith('{') && last.EndsWith('}'))
                    {
                        _parser.Feed(last);
                    }
                }

                return _parser.Result(path) with { LastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) };
            }
        }

        private void Feed()
        {
            var line = Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length).TrimEnd('\r');
            _partial.SetLength(0);
            if (line.Length > 0)
            {
                _parser.Feed(line);
            }
        }
    }

    private interface ILogParser
    {
        void Feed(string line);

        SessionLogMeasurement Result(string path);

        ILogParser Fresh();
    }

    private sealed class CodexParser : ILogParser
    {
        private bool _sawTurnContext;
        private int? _promptTokens, _firstInput, _lastInput, _window;

        public ILogParser Fresh() => new CodexParser();

        public void Feed(string line)
        {
            var isTurn = line.Contains("\"turn_context\"", StringComparison.Ordinal);
            var isCount = line.Contains("\"token_count\"", StringComparison.Ordinal);
            var maybePrompt = _sawTurnContext && _promptTokens is null && line.Contains("\"user\"", StringComparison.Ordinal);
            if (!isTurn && !isCount && !maybePrompt)
            {
                return;
            }

            using var doc = TryParse(line);
            if (doc is null)
            {
                return;
            }

            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type == "turn_context")
            {
                _sawTurnContext = true;
                return;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (type == "response_item" && _promptTokens is null && _sawTurnContext
                && Str(payload, "type") == "message" && Str(payload, "role") == "user")
            {
                _promptTokens = TokenEstimator.Estimate(Text(payload, "content"));
            }
            else if (type == "event_msg" && Str(payload, "type") == "token_count"
                     && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                     && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object
                     && Int(last, "input_tokens") is { } input)
            {
                _firstInput ??= input;
                _lastInput = input;
                _window = Int(info, "model_context_window") ?? _window;
            }
        }

        public SessionLogMeasurement Result(string path)
        {
            if (_firstInput is null)
            {
                return new SessionLogMeasurement(SessionLogState.WaitingFirstAnswer, path);
            }

            if (_promptTokens is null)
            {
                return new SessionLogMeasurement(SessionLogState.Unreadable, path);
            }

            return new SessionLogMeasurement(SessionLogState.Measured, path, Math.Max(0, _firstInput.Value - _promptTokens.Value), _firstInput,
                _promptTokens, _lastInput, _window);
        }
    }

    private sealed class ClaudeParser : ILogParser
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int? _promptTokens, _firstInput, _lastInput;
        private string? _model;

        public ILogParser Fresh() => new ClaudeParser();

        public void Feed(string line)
        {
            var isUser = _promptTokens is null && line.Contains("\"user\"", StringComparison.Ordinal);
            var isUsage = line.Contains("\"usage\"", StringComparison.Ordinal);
            if (!isUser && !isUsage)
            {
                return;
            }

            using var doc = TryParse(line);
            if (doc is null)
            {
                return;
            }

            var root = doc.RootElement;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
            {
                return;
            }

            var type = Str(root, "type");
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (type == "user" && _promptTokens is null && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                && UserText(message) is { } text)
            {
                _promptTokens = TokenEstimator.Estimate(text);
            }
            else if (type == "assistant" && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var id = Str(message, "id") ?? Str(root, "requestId") ?? Str(root, "uuid");
                if (id is not null && !_seen.Add(id))
                {
                    return;
                }

                var input = (Int(usage, "input_tokens") ?? 0) + (Int(usage, "cache_creation_input_tokens") ?? 0) + (Int(usage, "cache_read_input_tokens") ?? 0);
                if (input <= 0)
                {
                    return;
                }

                _firstInput ??= input;
                _lastInput = input;
                _model = Str(message, "model") ?? _model;
            }
        }

        public SessionLogMeasurement Result(string path)
        {
            if (_firstInput is null)
            {
                return new SessionLogMeasurement(SessionLogState.WaitingFirstAnswer, path);
            }

            if (_promptTokens is null)
            {
                return new SessionLogMeasurement(SessionLogState.Unreadable, path);
            }

            return new SessionLogMeasurement(SessionLogState.Measured, path, Math.Max(0, _firstInput.Value - _promptTokens.Value), _firstInput,
                _promptTokens, _lastInput, Model: _model);
        }

        /// <summary>The user's text: a string, or text parts of a list (a list with tool results is not a prompt).</summary>
        private static string? UserText(JsonElement message)
        {
            if (!message.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var text = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                var partType = part.ValueKind == JsonValueKind.Object ? Str(part, "type") : null;
                if (partType == "tool_result")
                {
                    return null;
                }

                if (partType == "text" && Str(part, "text") is { } value)
                {
                    text.Append(value);
                }
            }

            return text.Length > 0 ? text.ToString() : null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> ReadLines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return line.Length > 0 && line[0] == '{' ? JsonDocument.Parse(line) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? (int)Math.Clamp(number, 0, int.MaxValue)
            : null;

    private static string Text(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        var text = new StringBuilder();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (Str(part, "text") is { } value)
                {
                    text.Append(value);
                }
            }
        }

        return text.ToString();
    }

    /// <summary>Same file or folder, ignoring case, slash direction and a trailing separator.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a.Replace('/', '\\')), Path.TrimEndingDirectorySeparator(b.Replace('/', '\\')), StringComparison.OrdinalIgnoreCase);
}
