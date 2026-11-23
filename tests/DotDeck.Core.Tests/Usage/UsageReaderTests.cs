using System.Globalization;
using DotDeck.Core.Tests.Support;
using DotDeck.Core.Usage;

namespace DotDeck.Core.Tests.Usage;

public sealed class UsageReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly DateTime _day;

    public UsageReaderTests() => _day = _now.UtcDateTime.Date;

    public void Dispose() => _temp.Dispose();

    private string At(int hour, int minute = 0) =>
        _day.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private string Write(string relative, params string[] lines)
    {
        var path = _temp.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Aggregator_counts_each_response_once_per_local_day_and_finds_the_top_session()
    {
        var day = DateOnly.FromDateTime(_day);
        var t = new DateTimeOffset(_day, TimeSpan.Zero);
        TokenRecord[] records =
        [
            new("a", "s1", @"D:\one", t.AddHours(9), 100, 1000),
            new("a", "s1", @"D:\one", t.AddHours(9), 100, 1000), // same response logged again
            new("b", "s2", @"D:\two", t.AddHours(9).AddMinutes(30), 300, 0),
            new("c", "s1", @"D:\one", t.AddHours(14), 50, 10),
            new("d", "s2", @"D:\two", t.AddDays(-1), 9999, 0), // yesterday
        ];

        var summary = UsageAggregator.Summarize(records, day, TimeZoneInfo.Utc);

        Assert.Equal(450, summary.Total);
        Assert.Equal(1010, summary.CacheRead);
        Assert.Equal(400, summary.Hourly[9]);
        Assert.Equal(50, summary.Hourly[14]);
        Assert.Equal((@"D:\two", 300L, 2), (summary.TopSessionProject, summary.TopSessionTotal, summary.SessionCount));
    }

    [Fact]
    public void Claude_counts_input_output_and_cache_creation_and_reports_cache_reads_apart()
    {
        static string Assistant(string id, string time, int input, int output, int creation, int read) =>
            $$$$$$"""{"type":"assistant","timestamp":"{{{{{{time}}}}}}","sessionId":"s1","cwd":"D:\\work","requestId":"r-{{{{{{id}}}}}}","message":{"id":"{{{{{{id}}}}}}","usage":{"input_tokens":{{{{{{input}}}}}},"output_tokens":{{{{{{output}}}}}},"cache_creation_input_tokens":{{{{{{creation}}}}}},"cache_read_input_tokens":{{{{{{read}}}}}}}}}""";
        Write("claude/projects/D--work/s1.jsonl",
            """{"type":"user","timestamp":"2026-01-01T00:00:00Z","message":{"content":"usage"}}""",
            Assistant("m1", At(10), 2, 100, 1000, 5000),
            Assistant("m1", At(10), 2, 100, 1000, 5000), // one line per content block
            Assistant("m2", At(11), 1, 50, 0, 7000),
            """{"type":"assistant","timestamp":""");
        Write("claude/projects/D--work/s1/subagents/agent-1.jsonl", Assistant("m3", At(11, 30), 3, 7, 0, 0));
        File.WriteAllText(_temp.Combine("usage.json"), """{"measured_at":1795433619925,"five_hour":{"used_percentage":72.5,"resets_at":1795440000},"seven_day":{"used_percentage":16,"resets_at":"2026-11-27T13:00:00Z"}}""");

        var report = new ClaudeUsageReader(_temp.Combine("claude"), _temp.Combine("usage.json")).Read(_now, TimeZoneInfo.Utc)!;

        Assert.Equal(1102 + 51 + 10, report.Today.Total);
        Assert.Equal(12000, report.Today.CacheRead);
        Assert.Equal((@"D:\work", 1), (report.Today.TopSessionProject, report.Today.SessionCount));
        Assert.Equal(1102, report.Today.Hourly[10]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795433619925), report.Limits!.MeasuredAt);
        Assert.Equal(new LimitWindow(72.5, DateTimeOffset.FromUnixTimeSeconds(1795440000)), report.Limits.FiveHour);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 13, 0, 0, TimeSpan.Zero), report.Limits.SevenDay!.ResetsAt);
    }

    [Fact]
    public void Claude_without_logs_or_limits()
    {
        Assert.Null(new ClaudeUsageReader(_temp.Combine("none"), _temp.Combine("usage.json")).Read(_now, TimeZoneInfo.Utc));

        Directory.CreateDirectory(_temp.Combine("claude", "projects"));
        File.WriteAllText(_temp.Combine("usage.json"), "{broken");
        var report = new ClaudeUsageReader(_temp.Combine("claude"), _temp.Combine("usage.json")).Read(_now, TimeZoneInfo.Utc)!;

        Assert.Equal(TokenUsageSummary.Empty.Total, report.Today.Total);
        Assert.Null(report.Limits);
    }

    [Fact]
    public void Codex_subtracts_cached_input_and_does_not_add_reasoning_again()
    {
        Write("codex/sessions/2026/11/23/rollout-a.jsonl",
            $$$$$$"""{"timestamp":"{{{{{{At(9)}}}}}}","type":"session_meta","payload":{"id":"thread-1","cwd":"D:\\proj"}}""",
            $$$$$$"""{"timestamp":"{{{{{{At(9, 1)}}}}}}","type":"token_usage_record","payload":{"session_id":"thread-1","response_id":"resp-1","usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":120,"reasoning_output_tokens":100,"total_tokens":1120}}}""",
            $$$$$$"""{"timestamp":"{{{{{{At(9, 1)}}}}}}","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1120},"last_token_usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":120}},"rate_limits":{"limit_id":"codex","primary":{"used_percent":91.0,"window_minutes":300,"resets_at":1795450254},"secondary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1795784478}}}}""",
            $$$$$$"""{"timestamp":"{{{{{{At(9, 2)}}}}}}","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"other-model","primary":{"used_percent":5.0,"window_minutes":300}}}}""",
            $$$$$$"""{"timestamp":"{{{{{{At(9, 3)}}}}}}","type":"token_usage_record","payload":{"session_id":"thread-1","response_id":"resp-2","usage":{"input_tokens":500,"cached_input_tokens":0,"output_tokens":20,"reasoning_output_tokens":10}}}""");

        var report = new CodexUsageReader(_temp.Combine("codex")).Read(_now, TimeZoneInfo.Utc)!;

        Assert.Equal((1000 - 800 + 120) + (500 + 20), report.Today.Total);
        Assert.Equal(800, report.Today.CacheRead);
        Assert.Equal(@"D:\proj", report.Today.TopSessionProject);
        Assert.Equal(new LimitWindow(91.0, DateTimeOffset.FromUnixTimeSeconds(1795450254)), report.Limits!.FiveHour);
        Assert.Equal(3.0, report.Limits.SevenDay!.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse(At(9, 1), CultureInfo.InvariantCulture), report.Limits.MeasuredAt);
    }

    [Fact]
    public void Codex_logs_without_usage_records_count_the_growth_of_token_count()
    {
        static string Count(string time, long cumulative, long input, long cached, long output) =>
            $$$$$$"""{"timestamp":"{{{{{{time}}}}}}","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":{{{{{{cumulative}}}}}}},"last_token_usage":{"input_tokens":{{{{{{input}}}}}},"cached_input_tokens":{{{{{{cached}}}}}},"output_tokens":{{{{{{output}}}}}}}}}}""";
        Write("codex/sessions/2026/11/23/rollout-old.jsonl",
            $$$$$$"""{"timestamp":"{{{{{{At(8)}}}}}}","type":"session_meta","payload":{"id":"old","cwd":"D:\\old"}}""",
            Count(At(8, 1), 300, 250, 100, 50),
            Count(At(8, 1), 300, 250, 100, 50), // repeated event, no new response
            Count(At(8, 2), 700, 380, 300, 20));

        var report = new CodexUsageReader(_temp.Combine("codex")).Read(_now, TimeZoneInfo.Utc)!;

        Assert.Equal((250 - 100 + 50) + (380 - 300 + 20), report.Today.Total);
        Assert.Equal(400, report.Today.CacheRead);
        Assert.Null(report.Limits);
    }

    [Fact]
    public void Out_of_range_reset_times_are_unknown_instead_of_failing()
    {
        File.WriteAllText(_temp.Combine("usage.json"), """{"measured_at":1795433619925,"five_hour":{"used_percentage":50,"resets_at":1e300},"seven_day":{"used_percentage":1,"resets_at":-5}}""");

        var limits = ClaudeUsageReader.ReadLimits(_temp.Combine("usage.json"))!;

        Assert.Equal(new LimitWindow(50, null), limits.FiveHour);
        Assert.Equal(new LimitWindow(1, null), limits.SevenDay);
    }

    [Fact]
    public void A_day_without_midnight_starts_at_its_first_valid_time()
    {
        // Daylight saving time starts at 00:00 on March 1st: that day begins at 01:00 local (+02:00).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 10, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/MidnightDst", TimeSpan.FromHours(1), "Test", "Test", "Test DST", [rule]);

        var day = UsageDay.Of(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero), zone);

        Assert.Equal(new DateOnly(2026, 3, 1), day.Date);
        Assert.Equal(new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Utc), day.StartUtc);
    }

    [Fact]
    public void A_reset_window_counts_as_unused()
    {
        var window = new LimitWindow(80, _now.AddMinutes(-1));

        Assert.Equal(0, window.UsedPercentAt(_now));
        Assert.Equal(80, new LimitWindow(80, _now.AddMinutes(1)).UsedPercentAt(_now));
    }

    [Fact]
    public void Agy_counts_conversations_written_today_without_opening_them()
    {
        var dir = _temp.Combine("gemini", "antigravity-cli", "conversations");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.db"), "x");
        File.WriteAllText(Path.Combine(dir, "a.db-wal"), "x");
        File.WriteAllText(Path.Combine(dir, "b.db"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "b.db"), _day.AddDays(-2));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");

        var activity = new AgyActivityReader(_temp.Combine("gemini")).Read(_now, TimeZoneInfo.Utc)!;

        Assert.Equal(1, activity.ConversationsToday);
        Assert.True(activity.LastUsedAt >= new DateTimeOffset(_day, TimeSpan.Zero));
        Assert.Null(new AgyActivityReader(_temp.Combine("missing")).Read(_now, TimeZoneInfo.Utc));
    }
}
