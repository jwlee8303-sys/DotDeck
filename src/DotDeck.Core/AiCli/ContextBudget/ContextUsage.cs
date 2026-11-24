using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Usage;

namespace DotDeck.Core.AiCli.ContextBudget;

/// <summary>Where a session's context usage came from (shown in the tooltip).</summary>
public enum ContextUsageSource
{
    None,

    /// <summary>Codex's log: latest <c>last_token_usage.input_tokens</c> / <c>model_context_window</c> ("Codex 로그 실측").</summary>
    CodexLog,

    /// <summary>Claude Code's status line input (<c>context_window</c>), saved by DotDeck's script.</summary>
    ClaudeStatusLine,

    /// <summary>Claude's log (last answer's input) and the window from the model table ("추정").</summary>
    ClaudeLog,

    /// <summary>agy's status line input (<c>context_window.used_percentage</c>), saved by DotDeck's script ("agy 제공").</summary>
    AgyStatusLine,
}

/// <summary>Color of the "컨텍스트" column.</summary>
public enum ContextUsageLevel
{
    Normal,
    Warning,
    Danger,
}

/// <summary>
/// A session's current context usage. <paramref name="Percent"/> is only set when the window is known (reported, or
/// from the model table for Claude's log: <paramref name="Estimated"/>); otherwise only <paramref name="Tokens"/> is
/// shown. <paramref name="Reason"/> explains a fallback or a missing value (tooltip).
/// </summary>
public sealed record ContextUsage(
    SessionLogState State,
    ContextUsageSource Source = ContextUsageSource.None,
    int? Tokens = null,
    int? Window = null,
    double? Percent = null,
    bool Estimated = false,
    string? Model = null,
    string? Reason = null)
{
    /// <summary>"38%", "38% (추정)", "4.2만 토큰", "첫 응답 후 측정" or "측정 불가".</summary>
    public string Text => State switch
    {
        SessionLogState.Measured when Percent is { } percent =>
            string.Create(CultureInfo.InvariantCulture, $"{Shown(percent):0}%") + (Estimated ? " (추정)" : string.Empty),
        SessionLogState.Measured when Tokens is { } tokens => TokenEstimator.Format(tokens) + " 토큰",
        SessionLogState.WaitingFirstAnswer => "첫 응답 후 측정",
        _ => "측정 불가",
    };

    /// <summary>Orange/red from the settings' thresholds; a value without a percentage is never colored.</summary>
    public ContextUsageLevel Level(ContextBudgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (State != SessionLogState.Measured || Percent is not { } percent)
        {
            return ContextUsageLevel.Normal;
        }

        // The shown (rounded) number decides the color: "70%" is never left uncolored at a 70% threshold.
        var shown = Shown(percent);
        return shown >= settings.UsageDangerPercent ? ContextUsageLevel.Danger
            : shown >= settings.UsageWarningPercent ? ContextUsageLevel.Warning
            : ContextUsageLevel.Normal;
    }

    private static double Shown(double percent) => Math.Round(percent, MidpointRounding.AwayFromZero);

    /// <summary>Source and values: "Codex 로그 실측", "agy 제공", "Claude: 모델명 기준 추정(창 200k)", …</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string>();
            switch (Source)
            {
                case ContextUsageSource.CodexLog:
                    lines.Add("Codex 로그 실측");
                    break;
                case ContextUsageSource.AgyStatusLine:
                    lines.Add("agy 제공");
                    break;
                case ContextUsageSource.ClaudeStatusLine:
                    lines.Add("Claude 상태 표시줄 제공" + (Window is { } w ? $"(창 {WindowText(w)})" : string.Empty));
                    break;
                case ContextUsageSource.ClaudeLog:
                    lines.Add(Window is { } window
                        ? $"Claude: {Model ?? "모델 모름"} 기준 추정(창 {WindowText(window)})"
                        : $"Claude 로그 실측 · {Model ?? "모델 모름"} (창 크기를 몰라 %는 계산하지 않음)");
                    break;
            }

            if (State == SessionLogState.Measured && Tokens is { } tokens)
            {
                lines.Add("현재 입력 " + tokens.ToString("N0", CultureInfo.InvariantCulture) + " 토큰"
                          + (Window is { } size ? " / " + size.ToString("N0", CultureInfo.InvariantCulture) : string.Empty));
            }

            if (Reason is { Length: > 0 })
            {
                lines.Add(Reason);
            }

            return lines.Count > 0 ? string.Join("\n", lines) : Text;
        }
    }

    /// <summary>"200k", "1M".</summary>
    public static string WindowText(int window) => window % 1_000_000 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{window / 1_000_000}M")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(window / 1000.0):0}k");
}

/// <summary>One session's values saved by a DotDeck status line script (no conversation text).</summary>
public sealed record StatusLineContext(
    string SessionId,
    string? TranscriptPath,
    string? Cwd,
    string? Model,
    int? Window,
    double? Percent,
    int? InputTokens,
    DateTimeOffset MeasuredAt,
    DateTimeOffset? FirstSeen = null);

/// <summary>
/// Reads <c>~/.dotdeck/context/&lt;claude|agy&gt;-&lt;session id&gt;.json</c>, written by DotDeck's status line scripts
/// (<see cref="StatusLineInstaller"/>). Unreadable files count as missing.
/// </summary>
public sealed partial class StatusLineContextStore(string dotDeckHome)
{
    public const string Claude = "claude";
    public const string Agy = "agy";

    private static readonly TimeSpan s_earlyTolerance = TimeSpan.FromSeconds(30);

    public string Directory => Path.Combine(dotDeckHome, "context");

    /// <summary>agy's status line runs DotDeck's script (its file is there; the settings are checked by the installer).</summary>
    public bool AgyConnected => File.Exists(Path.Combine(dotDeckHome, AgyStatusLineInstaller.FileName));

    public StatusLineContext? Read(string prefix, string? sessionId) =>
        sessionId is not null && SafeId().IsMatch(sessionId) ? Parse(Path.Combine(Directory, prefix + "-" + sessionId + ".json")) : null;

    /// <summary>The Claude session whose status line reported <paramref name="transcriptPath"/>.</summary>
    public StatusLineContext? FindByTranscript(string transcriptPath) =>
        All(Claude).FirstOrDefault(c => c.TranscriptPath is { } path && SessionLogReader.SamePath(path, transcriptPath));

    /// <summary>
    /// The session in <paramref name="cwd"/> whose status line first reported within <see cref="SessionLogReader.MatchWindow"/>
    /// after <paramref name="startedAt"/> (a session started elsewhere earlier, or resumed, is never taken), not one of
    /// <paramref name="claimed"/> (sessions other rows already use). Two candidates: ambiguous. Values without a first
    /// report time (older scripts) are not matched by folder.
    /// </summary>
    public (StatusLineContext? Found, bool Ambiguous) Find(string prefix, string? cwd, DateTimeOffset startedAt, IReadOnlySet<string>? claimed)
    {
        if (cwd is null)
        {
            return (null, false);
        }

        var candidates = All(prefix)
            .Where(c => c.Cwd is { } folder && SessionLogReader.SamePath(folder, cwd)
                        && c.FirstSeen is { } first && first >= startedAt - s_earlyTolerance && first <= startedAt + SessionLogReader.MatchWindow
                        && claimed?.Contains(c.SessionId) != true)
            .ToList();
        return candidates.Count switch
        {
            1 => (candidates[0], false),
            0 => (null, false),
            _ => (null, true),
        };
    }

    /// <summary>Deletes values not updated for <paramref name="olderThan"/> and leftover temporary files.</summary>
    public void Prune(DateTimeOffset now, TimeSpan olderThan)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            var name = Path.GetFileName(file);
            if (!(name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) > olderThan)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use: next time
            }
        }
    }

    private IEnumerable<StatusLineContext> All(string prefix)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            yield break;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, prefix + "-*.json"))
        {
            if (Parse(file) is { } context)
            {
                yield return context;
            }
        }
    }

    private static StatusLineContext? Parse(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "session_id") is not { Length: > 0 } id
                || Num(root, "measured_at") is not { } measured)
            {
                return null;
            }

            var window = Num(root, "context_window_size") is { } size && size is > 0 and < int.MaxValue ? (int?)size : null;
            var percent = Num(root, "used_percentage") is { } p && p is >= 0 and <= 1000 ? (double?)p : null;
            var input = Num(root, "input_tokens") is { } t && t is >= 0 and < int.MaxValue ? (int?)t : null;
            return new StatusLineContext(id, Str(root, "transcript_path"), Str(root, "cwd"), Str(root, "model"), window, percent, input,
                Time(measured), Num(root, "first_seen") is { } first ? Time(first) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset Time(double milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(milliseconds, 0, 253_402_300_799_000));

    private static string? Str(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Num(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// What to read for one session. <paramref name="LogPath"/> and <paramref name="StatusLineId"/> are what an earlier
/// read matched (kept by the caller so a session stays on its file); <paramref name="Claimed"/> are status line
/// sessions other rows use.
/// </summary>
public sealed record ContextUsageQuery(SessionLogQuery Log, string? LogPath = null, string? StatusLineId = null, IReadOnlySet<string>? Claimed = null);

/// <summary>The usage and what it was read from (to pass back in the next <see cref="ContextUsageQuery"/>).</summary>
public sealed record ContextUsageResult(ContextUsage Usage, string? LogPath, string? StatusLineId);

/// <summary>
/// Picks a session's context usage per CLI:
/// <list type="bullet">
/// <item>Codex: its log (tokens / reported window).</item>
/// <item>Claude: the status line value when there is one; the log and the model table otherwise (non-interactive
/// <c>-p</c> runs and status lines DotDeck is not connected to have none), and also when the status line value is
/// older than <see cref="StatusLineStaleAfter"/> while the log has moved on since. Model not in the table: tokens only.</item>
/// <item>agy: the status line value only.</item>
/// </list>
/// A percentage is never made up: without a known window only tokens are shown.
/// </summary>
public sealed class ContextUsageReader(SessionLogReader logs, StatusLineContextStore statusLines)
{
    public static readonly TimeSpan StatusLineStaleAfter = TimeSpan.FromMinutes(5);

    public ContextUsageResult Read(ContextUsageQuery query, ContextBudgetSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(settings);
        return query.Log.Cli switch
        {
            CliKind.Codex => ReadCodex(query),
            CliKind.Claude => ReadClaude(query, settings, now),
            _ => ReadAgy(query),
        };
    }

    private ContextUsageResult ReadCodex(ContextUsageQuery query)
    {
        var (log, path) = ReadLog(query, query.LogPath, []);
        if (log.State != SessionLogState.Measured)
        {
            return new ContextUsageResult(Unmeasured(log.State, "Codex 로그"), path, null);
        }

        var percent = log.CurrentInput is { } input && log.ContextWindow is > 0 and { } window ? input * 100.0 / window : (double?)null;
        return new ContextUsageResult(new ContextUsage(SessionLogState.Measured, ContextUsageSource.CodexLog, log.CurrentInput, log.ContextWindow, percent,
            Reason: percent is null ? "로그에 창 크기가 없어 %는 계산하지 않음" : null), path, null);
    }

    private ContextUsageResult ReadClaude(ContextUsageQuery query, ContextBudgetSettings settings, DateTimeOffset now)
    {
        var id = query.StatusLineId ?? query.Log.ConversationId;
        var line = statusLines.Read(StatusLineContextStore.Claude, id);
        var known = query.LogPath ?? (line?.TranscriptPath is { } transcript && File.Exists(transcript) ? transcript : null);
        var (log, path) = ReadLog(query, known, settings.EffectiveClaudeContextWindows);

        if (line is null && path is not null)
        {
            line = statusLines.FindByTranscript(path);
        }

        if (line is null && path is null && log.State is SessionLogState.NotMatched or SessionLogState.WaitingFirstAnswer)
        {
            // Two candidate logs in the folder: the status line knows which conversation this terminal runs.
            var (found, ambiguous) = statusLines.Find(StatusLineContextStore.Claude, query.Log.WorkingDirectory, query.Log.StartedAt, query.Claimed);
            line = found;
            if (found?.TranscriptPath is { } foundTranscript && File.Exists(foundTranscript))
            {
                (log, path) = ReadLog(query, foundTranscript, settings.EffectiveClaudeContextWindows);
            }
            else if (ambiguous)
            {
                log = new SessionLogMeasurement(SessionLogState.NotMatched);
            }
        }

        var lineId = line?.SessionId;
        string? reason = null;
        // Right after /compact Claude Code reports 0 with no usage until the next call: not a value.
        if (line is { Percent: { } percent, Window: { } window } && !(percent == 0 && line.InputTokens is null or 0))
        {
            var stale = now - line.MeasuredAt > StatusLineStaleAfter;
            if (!(stale && log is { State: SessionLogState.Measured, LastWrite: { } written } && written > line.MeasuredAt))
            {
                return new ContextUsageResult(new ContextUsage(SessionLogState.Measured, ContextUsageSource.ClaudeStatusLine, line.InputTokens, window, percent,
                    Model: line.Model), path, lineId);
            }

            reason = "상태 표시줄 값이 5분 넘게 갱신되지 않아 더 최신인 로그 값 사용";
        }
        else
        {
            reason = line is null ? "상태 표시줄 값 없음(연결 안 됨 또는 비대화형 세션) → 로그 사용" : "상태 표시줄 값이 아직 비어 있음 → 로그 사용";
        }

        if (log.State != SessionLogState.Measured)
        {
            return new ContextUsageResult(Unmeasured(log.State, "Claude 로그"), path, lineId);
        }

        if (log.ContextWindow is null && SessionLogReader.WindowFor(log.Model, settings.EffectiveClaudeContextWindows) is { } tableWindow)
        {
            reason = $"현재 입력이 표의 창 크기({ContextUsage.WindowText(tableWindow)})보다 커서 표가 이 모델과 맞지 않음 → 환경 설정 → AI 컨텍스트에서 창 크기 수정\n" + reason;
        }
        else if (log.ContextWindow is null)
        {
            reason = "모델이 창 크기 표에 없음 → 환경 설정 → AI 컨텍스트에서 추가\n" + reason;
        }

        var estimate = log.ContextWindow is { } table && log.CurrentInput is { } input ? input * 100.0 / table : (double?)null;
        return new ContextUsageResult(new ContextUsage(SessionLogState.Measured, ContextUsageSource.ClaudeLog, log.CurrentInput, log.ContextWindow, estimate,
            Estimated: estimate is not null, Model: log.Model, Reason: reason), path, lineId);
    }

    private ContextUsageResult ReadAgy(ContextUsageQuery query)
    {
        var line = statusLines.Read(StatusLineContextStore.Agy, query.StatusLineId);
        var ambiguous = false;
        if (line is null)
        {
            (line, ambiguous) = statusLines.Find(StatusLineContextStore.Agy, query.Log.WorkingDirectory, query.Log.StartedAt, query.Claimed);
        }

        if (line is null)
        {
            var usage = !statusLines.AgyConnected
                ? new ContextUsage(SessionLogState.Unreadable, Reason: "agy 사용률 연결 안 됨 (사용량 → \"agy 사용률 연결…\")")
                : ambiguous
                    ? new ContextUsage(SessionLogState.NotMatched, Reason: "같은 폴더의 agy 대화가 여러 개라 매칭 불가")
                    : new ContextUsage(SessionLogState.WaitingFirstAnswer, Reason: "agy 상태 표시줄 값을 기다리는 중");
            return new ContextUsageResult(usage, null, query.StatusLineId);
        }

        var result = line.Percent is { } percent
            ? new ContextUsage(SessionLogState.Measured, ContextUsageSource.AgyStatusLine, line.InputTokens, line.Window, percent, Model: line.Model)
            : new ContextUsage(SessionLogState.WaitingFirstAnswer, ContextUsageSource.AgyStatusLine, Reason: "agy가 사용률을 아직 보내지 않음");
        return new ContextUsageResult(result, null, line.SessionId);
    }

    /// <summary>Reads the known log (matching again when it is gone), or matches the session's log first.</summary>
    private (SessionLogMeasurement Log, string? Path) ReadLog(ContextUsageQuery query, string? known, IReadOnlyList<ModelWindowRule> windows)
    {
        if (known is not null)
        {
            var log = logs.ReadFile(query.Log.Cli, known, windows);
            if (log.State != SessionLogState.NotMatched)
            {
                return (log, known);
            }
        }

        var match = logs.Match(query.Log);
        return match is { State: SessionLogState.Measured, Path: { } path }
            ? (logs.ReadFile(query.Log.Cli, path, windows), path)
            : (new SessionLogMeasurement(match.State), null);
    }

    private static ContextUsage Unmeasured(SessionLogState state, string what) => new(state, Reason: state switch
    {
        SessionLogState.WaitingFirstAnswer => what + "에 아직 응답 기록 없음",
        SessionLogState.NotMatched => what + " 매칭 불가 (같은 폴더·시간대의 로그가 여러 개이거나 없음)",
        SessionLogState.Unreadable => what + " 형식을 읽지 못함",
        _ => null,
    });
}
