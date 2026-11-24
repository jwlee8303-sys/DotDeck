using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class ContextUsageTests : IDisposable
{
    private static readonly ContextBudgetSettings s_settings = new();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Claude => _temp.Combine("claude");

    private string Codex => _temp.Combine("codex");

    private string DotDeckHome => _temp.Combine("dotdeck");

    private static string Line(JsonObject node) => node.ToJsonString() + "\n";

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonObject TokenCount(int input, int? window) => new()
    {
        ["type"] = "event_msg",
        ["payload"] = new JsonObject
        {
            ["type"] = "token_count",
            ["info"] = new JsonObject
            {
                ["last_token_usage"] = new JsonObject { ["input_tokens"] = input },
                ["model_context_window"] = window,
            },
        },
    };

    private string CodexLog(DateTimeOffset started, string cwd, params JsonObject[] counts)
    {
        var dir = Path.Combine(Codex, "sessions", started.ToString("yyyy"), started.ToString("MM"), started.ToString("dd"));
        var text = Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["cwd"] = cwd, ["timestamp"] = started.ToString("O") } })
            + Line(new JsonObject { ["type"] = "turn_context", ["payload"] = new JsonObject() })
            + Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "hi" } })
            + string.Concat(counts.Select(Line));
        return Write(Path.Combine(dir, $"rollout-{started:yyyy-MM-ddTHH-mm-ss}-t1.jsonl"), text);
    }

    private static JsonObject Assistant(string id, int cacheRead, string model = "claude-opus-5-5") => new()
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["id"] = id,
            ["model"] = model,
            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = cacheRead },
        },
    };

    private string ClaudeLog(string sessionId, DateTimeOffset started, params JsonObject[] answers) =>
        Write(Path.Combine(Claude, "projects", "D--w", sessionId + ".jsonl"),
            Line(new JsonObject { ["type"] = "user", ["timestamp"] = started.ToString("O"), ["message"] = new JsonObject { ["content"] = "hello" } })
            + string.Concat(answers.Select(Line)));

    private void StatusLine(string prefix, string id, string? transcript, double? percent, int? window, DateTimeOffset measured, string cwd = @"D:\w", int? input = null, DateTimeOffset? firstSeen = null) =>
        Write(Path.Combine(DotDeckHome, "context", $"{prefix}-{id}.json"), new JsonObject
        {
            ["session_id"] = id,
            ["transcript_path"] = transcript,
            ["cwd"] = cwd,
            ["model"] = "claude-opus-5-5",
            ["context_window_size"] = window,
            ["used_percentage"] = percent,
            ["input_tokens"] = input,
            ["measured_at"] = measured.ToUnixTimeMilliseconds(),
            ["first_seen"] = (firstSeen ?? measured).ToUnixTimeMilliseconds(),
        }.ToJsonString());

    private ContextUsageReader Reader(SessionLogReader? logs = null) =>
        new(logs ?? new SessionLogReader(Claude, Codex), new StatusLineContextStore(DotDeckHome));

    // ---------------------------------------------------------------- display

    [Fact]
    public void Text_level_and_tooltip_follow_the_source()
    {
        var codex = new ContextUsage(SessionLogState.Measured, ContextUsageSource.CodexLog, 180_000, 475_000, 37.9);
        Assert.Equal("38%", codex.Text);
        Assert.Equal(ContextUsageLevel.Normal, codex.Level(s_settings));
        Assert.StartsWith("Codex 로그 실측", codex.ToolTip, StringComparison.Ordinal);

        var claude = new ContextUsage(SessionLogState.Measured, ContextUsageSource.ClaudeLog, 150_000, 200_000, 75, Estimated: true, Model: "claude-opus-5-5");
        Assert.Equal("75% (추정)", claude.Text);
        Assert.Equal(ContextUsageLevel.Warning, claude.Level(s_settings));
        Assert.StartsWith("Claude: claude-opus-5-5 기준 추정(창 200k)", claude.ToolTip, StringComparison.Ordinal);

        var agy = new ContextUsage(SessionLogState.Measured, ContextUsageSource.AgyStatusLine, Percent: 85);
        Assert.Equal(ContextUsageLevel.Danger, agy.Level(s_settings));
        Assert.Equal(ContextUsageLevel.Warning, agy.Level(s_settings with { UsageDangerPercent = 90 }));
        Assert.StartsWith("agy 제공", agy.ToolTip, StringComparison.Ordinal);

        var tokensOnly = new ContextUsage(SessionLogState.Measured, ContextUsageSource.ClaudeLog, 42_000, Model: "other");
        Assert.Equal("4.2만 토큰", tokensOnly.Text);
        Assert.Equal(ContextUsageLevel.Normal, tokensOnly.Level(s_settings with { UsageWarningPercent = 1 }));

        Assert.Equal("첫 응답 후 측정", new ContextUsage(SessionLogState.WaitingFirstAnswer).Text);
        Assert.Equal("측정 불가", new ContextUsage(SessionLogState.NotMatched).Text);
        Assert.Equal("측정 불가", new ContextUsage(SessionLogState.Unreadable).Text);
        Assert.Equal("1M", ContextUsage.WindowText(1_000_000));

        // The shown number decides the color; halves round up.
        var edge = new ContextUsage(SessionLogState.Measured, ContextUsageSource.CodexLog, Percent: 69.6);
        Assert.Equal("70%", edge.Text);
        Assert.Equal(ContextUsageLevel.Warning, edge.Level(s_settings));
        Assert.Equal("39%", new ContextUsage(SessionLogState.Measured, ContextUsageSource.CodexLog, Percent: 38.5).Text);
    }

    // ---------------------------------------------------------------- Codex

    [Fact]
    public void Codex_uses_the_latest_token_count_and_without_a_window_shows_tokens_only()
    {
        var started = DateTimeOffset.Now;
        var path = CodexLog(started, @"D:\w", TokenCount(20_000, 400_000), TokenCount(152_000, 400_000));
        var query = new ContextUsageQuery(new SessionLogQuery(CliKind.Codex, @"D:\w", started));

        var result = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);

        Assert.Equal(path, result.LogPath);
        Assert.Equal("38%", result.Usage.Text);
        Assert.Equal(ContextUsageSource.CodexLog, result.Usage.Source);

        // A later count without the window keeps the last reported one (same model).
        File.AppendAllText(path, Line(TokenCount(40_000, null)));
        Assert.Equal("10%", Reader().Read(query, s_settings, DateTimeOffset.UtcNow).Usage.Text);

        // A log that never reported a window: tokens only.
        var other = DateTimeOffset.Now.AddSeconds(1);
        CodexLog(other, @"D:\x", TokenCount(30_000, null));
        var noWindow = Reader().Read(new ContextUsageQuery(new SessionLogQuery(CliKind.Codex, @"D:\x", other)), s_settings, DateTimeOffset.UtcNow);
        Assert.Equal("3만 토큰", noWindow.Usage.Text);
    }

    // ---------------------------------------------------------------- incremental reading

    [Fact]
    public void Log_is_read_from_the_last_position_and_starts_over_when_it_shrinks_or_disappears()
    {
        var started = DateTimeOffset.Now;
        var path = CodexLog(started, @"D:\w", TokenCount(20_000, 100_000));
        var logs = new SessionLogReader(Claude, Codex);

        Assert.Equal(20_000, logs.ReadFile(CliKind.Codex, path, []).CurrentInput);

        // Appended in two pieces: the half-written line waits for its end.
        var next = Line(TokenCount(50_000, 100_000));
        File.AppendAllText(path, next[..10]);
        Assert.Equal(20_000, logs.ReadFile(CliKind.Codex, path, []).CurrentInput);
        File.AppendAllText(path, next[10..]);
        var grown = logs.ReadFile(CliKind.Codex, path, []);
        Assert.Equal(50_000, grown.CurrentInput);
        Assert.Equal(20_000, grown.FirstRequestInput);

        // Shrunk (rewritten shorter): read from the start.
        CodexLog(started, @"D:\w", TokenCount(7_000, 100_000));
        var rewritten = logs.ReadFile(CliKind.Codex, path, []);
        Assert.Equal(7_000, rewritten.CurrentInput);
        Assert.Equal(7_000, rewritten.FirstRequestInput);

        // Gone: the caller matches again.
        File.Delete(path);
        Assert.Equal(SessionLogState.NotMatched, logs.ReadFile(CliKind.Codex, path, []).State);
    }

    [Fact]
    public void A_moved_log_is_matched_again()
    {
        var started = DateTimeOffset.Now;
        var path = CodexLog(started, @"D:\w", TokenCount(20_000, 100_000));
        var reader = Reader();
        var query = new ContextUsageQuery(new SessionLogQuery(CliKind.Codex, @"D:\w", started));
        var first = reader.Read(query, s_settings, DateTimeOffset.UtcNow);

        var moved = path.Replace("-t1.jsonl", "-t2.jsonl", StringComparison.Ordinal);
        File.Move(path, moved);
        var again = reader.Read(query with { LogPath = first.LogPath }, s_settings, DateTimeOffset.UtcNow);

        Assert.Equal(moved, again.LogPath);
        Assert.Equal("20%", again.Usage.Text);
    }

    // ---------------------------------------------------------------- Claude

    [Fact]
    public void Claude_prefers_the_status_line_value_found_through_the_transcript()
    {
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString();
        var log = ClaudeLog(id, started, Assistant("m1", 60_000));
        StatusLine("claude", id, log, 38, 1_000_000, DateTimeOffset.UtcNow, input: 380_000);

        // A terminal run: no session id, found by folder and start time, then by transcript path.
        var result = Reader().Read(new ContextUsageQuery(new SessionLogQuery(CliKind.Claude, @"D:\w", started)), s_settings, DateTimeOffset.UtcNow);

        Assert.Equal(ContextUsageSource.ClaudeStatusLine, result.Usage.Source);
        Assert.Equal("38%", result.Usage.Text);
        Assert.Equal(id, result.StatusLineId);
        Assert.StartsWith("Claude 상태 표시줄 제공(창 1M)", result.Usage.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_uses_the_log_and_model_table_without_a_status_line_value_or_when_it_is_stale()
    {
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString();
        var log = ClaudeLog(id, started, Assistant("m1", 50_000));
        var query = new ContextUsageQuery(new SessionLogQuery(CliKind.Claude, @"D:\w", started, id));

        // No status line (-p run, not connected): log + table.
        var fromLog = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal(ContextUsageSource.ClaudeLog, fromLog.Usage.Source);
        Assert.Equal("25% (추정)", fromLog.Usage.Text);
        Assert.Contains("상태 표시줄 값 없음", fromLog.Usage.ToolTip, StringComparison.Ordinal);

        // A model not in the table: tokens only, no made-up percentage.
        var unknown = Reader().Read(query, s_settings with { ClaudeContextWindows = [new("gpt-", 100_000)] }, DateTimeOffset.UtcNow);
        Assert.Equal("5만 토큰", unknown.Usage.Text);
        Assert.Null(unknown.Usage.Percent);

        // A status line value older than 5 minutes while the log moved on since: the log wins.
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        StatusLine("claude", id, log, 10, 200_000, old);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow);
        var stale = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal(ContextUsageSource.ClaudeLog, stale.Usage.Source);
        Assert.Contains("5분 넘게", stale.Usage.ToolTip, StringComparison.Ordinal);

        // Old but the log has not changed since: the status line value stays.
        File.SetLastWriteTimeUtc(log, old.UtcDateTime.AddMinutes(-1));
        var quiet = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal(ContextUsageSource.ClaudeStatusLine, quiet.Usage.Source);
        Assert.Equal("10%", quiet.Usage.Text);
    }

    [Fact]
    public void Claude_status_line_without_a_percentage_yet_falls_back_to_the_log()
    {
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString();
        ClaudeLog(id, started);
        StatusLine("claude", id, null, null, 200_000, DateTimeOffset.UtcNow);

        var result = Reader().Read(new ContextUsageQuery(new SessionLogQuery(CliKind.Claude, @"D:\w", started, id)), s_settings, DateTimeOffset.UtcNow);

        Assert.Equal(SessionLogState.WaitingFirstAnswer, result.Usage.State);
        Assert.Equal("첫 응답 후 측정", result.Usage.Text);
    }

    // ---------------------------------------------------------------- agy

    [Fact]
    public void Agy_uses_its_status_line_value_and_says_when_it_is_not_connected()
    {
        var started = DateTimeOffset.UtcNow;
        var query = new ContextUsageQuery(new SessionLogQuery(CliKind.Antigravity, @"D:\w", started));

        var off = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal("측정 불가", off.Usage.Text);
        Assert.Contains("연결 안 됨", off.Usage.ToolTip, StringComparison.Ordinal);

        Write(Path.Combine(DotDeckHome, Core.Usage.AgyStatusLineInstaller.FileName), "# script");
        Assert.Equal("첫 응답 후 측정", Reader().Read(query, s_settings, DateTimeOffset.UtcNow).Usage.Text);

        StatusLine("agy", "conv-1", null, 72.4, null, DateTimeOffset.UtcNow);
        var measured = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal("72%", measured.Usage.Text);
        Assert.Equal(ContextUsageLevel.Warning, measured.Usage.Level(s_settings));
        Assert.Equal("conv-1", measured.StatusLineId);

        // A second conversation in the same folder: ambiguous unless the other is claimed by another row.
        StatusLine("agy", "conv-2", null, 10, null, DateTimeOffset.UtcNow);
        Assert.Equal("측정 불가", Reader().Read(query, s_settings, DateTimeOffset.UtcNow).Usage.Text);
        Assert.Equal("10%", Reader().Read(query with { Claimed = new HashSet<string> { "conv-1" } }, s_settings, DateTimeOffset.UtcNow).Usage.Text);
        Assert.Equal("72%", Reader().Read(query with { StatusLineId = "conv-1" }, s_settings, DateTimeOffset.UtcNow).Usage.Text);
    }

    [Fact]
    public void A_session_that_first_reported_before_this_one_started_is_never_matched_by_folder()
    {
        Write(Path.Combine(DotDeckHome, Core.Usage.AgyStatusLineInstaller.FileName), "# script");
        var started = DateTimeOffset.UtcNow;
        var query = new ContextUsageQuery(new SessionLogQuery(CliKind.Antigravity, @"D:\w", started));

        // Another agy in the same folder (outside the monitor) still updating, but it started an hour ago.
        StatusLine("agy", "elsewhere", null, 90, null, DateTimeOffset.UtcNow, firstSeen: started.AddHours(-1));
        var result = Reader().Read(query, s_settings, DateTimeOffset.UtcNow);
        Assert.Equal("첫 응답 후 측정", result.Usage.Text);
        Assert.Null(result.StatusLineId);

        // Same for Claude: no log matched, and the only status line value belongs to an older session.
        var claudeQuery = new ContextUsageQuery(new SessionLogQuery(CliKind.Claude, @"D:\w", started));
        StatusLine("claude", "older", null, 90, 200_000, DateTimeOffset.UtcNow, firstSeen: started.AddHours(-1));
        Assert.Null(Reader().Read(claudeQuery, s_settings, DateTimeOffset.UtcNow).StatusLineId);
    }

    [Fact]
    public void Claude_zero_right_after_compact_is_not_a_value()
    {
        var started = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString();
        var log = ClaudeLog(id, started, Assistant("m1", 50_000));
        StatusLine("claude", id, log, 0, 200_000, DateTimeOffset.UtcNow, input: 0);

        var result = Reader().Read(new ContextUsageQuery(new SessionLogQuery(CliKind.Claude, @"D:\w", started, id)), s_settings, DateTimeOffset.UtcNow);

        Assert.Equal(ContextUsageSource.ClaudeLog, result.Usage.Source);
        Assert.Equal("25% (추정)", result.Usage.Text);
    }

    [Fact]
    public void Store_ignores_unsafe_ids_and_prunes_old_values()
    {
        var store = new StatusLineContextStore(DotDeckHome);
        StatusLine("agy", "conv-1", null, 50, null, DateTimeOffset.UtcNow);
        Assert.Null(store.Read("agy", "../conv-1"));
        Assert.NotNull(store.Read("agy", "conv-1"));

        File.SetLastWriteTimeUtc(Path.Combine(DotDeckHome, "context", "agy-conv-1.json"), DateTime.UtcNow.AddDays(-8));
        store.Prune(DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        Assert.Null(store.Read("agy", "conv-1"));
    }
}
