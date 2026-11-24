using System.IO;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.Usage;

namespace DotDeck.App.Tests;

public sealed class UsageDashboardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 14, 30, 0, TimeSpan.Zero);

    private static TokenUsageReport Report(double? fivePercent, DateTimeOffset? resetsAt = null)
    {
        var hourly = new long[24];
        hourly[9] = 1_000;
        hourly[14] = 4_000;
        var today = new TokenUsageSummary(5_000, 1_200_000, hourly, @"D:\work\DotDeck", 4_500, 2);
        var limits = fivePercent is { } p ? new ServerLimits(new LimitWindow(p, resetsAt ?? Now.AddHours(2)), null, Now.AddMinutes(-3)) : null;
        return new TokenUsageReport(today, limits);
    }

    private static UsageDashboardViewModel Dashboard(UsageSources sources, Clock? clock = null) =>
        new(sources, folder => Path.GetFileName(folder) ?? "?", clock ?? new Clock(Now), TimeZoneInfo.Utc);

    [Theory]
    [InlineData(69.9, UsageLevel.Normal)]
    [InlineData(70, UsageLevel.Warning)]
    [InlineData(89.9, UsageLevel.Warning)]
    [InlineData(90, UsageLevel.Critical)]
    public void Limit_levels(double percent, UsageLevel expected) => Assert.Equal(expected, UsageCardViewModel.LevelOf(percent));

    [Fact]
    public async Task Cards_show_tokens_limits_with_their_update_time_and_the_busiest_session()
    {
        var dashboard = Dashboard(new UsageSources((_, _) => Report(91), (_, _) => Report(null), (_, _) => new AgyActivity(3, Now.AddMinutes(-10))));

        await dashboard.RefreshAsync();

        var claude = dashboard.Claude;
        Assert.True(claude.HasData);
        Assert.Equal("오늘 5K 토큰 · 캐시 읽기 1.2M", claude.TodayText);
        Assert.Equal("91%", claude.PercentText);
        Assert.Equal("5시간 한도 91% · 리셋 16:30 · 마지막 갱신 14:27", claude.LimitText);
        Assert.Equal(UsageLevel.Critical, claude.Level);
        Assert.Equal("최다 세션: DotDeck (4.5K) · 세션 2개", claude.DetailText);
        Assert.Equal(24, claude.Bars.Count);
        Assert.Equal(UsageCardViewModel.GraphHeight, claude.Bars[14].Height);
        Assert.Equal(UsageCardViewModel.GraphHeight / 4, claude.Bars[9].Height);

        Assert.Equal(string.Empty, dashboard.Codex.PercentText); // no limits in its logs
        Assert.Equal(UsageLevel.Normal, dashboard.Codex.Level);
        Assert.Equal("오늘 대화 3개", dashboard.Agy.TodayText);
        Assert.Equal("마지막 사용 14:20", dashboard.Agy.DetailText);
        Assert.Equal("갱신 14:30", dashboard.RefreshedText);
    }

    [Fact]
    public async Task A_window_past_its_reset_shows_zero()
    {
        var dashboard = Dashboard(new UsageSources((_, _) => Report(95, Now.AddMinutes(-1)), (_, _) => null, (_, _) => null));

        await dashboard.RefreshAsync();

        Assert.Equal("0%", dashboard.Claude.PercentText);
        Assert.StartsWith("5시간 한도 0% · 리셋됨", dashboard.Claude.LimitText, StringComparison.Ordinal);
        Assert.Equal(UsageLevel.Normal, dashboard.Claude.Level);
    }

    [Fact]
    public async Task A_source_that_fails_shows_no_data_and_the_others_still_update()
    {
        var dashboard = Dashboard(new UsageSources(
            (_, _) => throw new UnauthorizedAccessException("denied"),
            (_, _) => Report(75),
            (_, _) => throw new IOException("locked")));

        await dashboard.RefreshAsync();

        Assert.False(dashboard.Claude.HasData);
        Assert.False(dashboard.Agy.HasData);
        Assert.True(dashboard.Codex.HasData);
        Assert.Equal(UsageLevel.Warning, dashboard.Codex.Level);
    }

    [Fact]
    public async Task Out_of_range_data_in_one_source_does_not_stop_the_others()
    {
        var dashboard = Dashboard(new UsageSources(
            (_, _) => throw new ArgumentOutOfRangeException("resets_at"),
            (_, _) => Report(10),
            (_, _) => throw new ArgumentException("invalid time")));

        await dashboard.RefreshAsync();

        Assert.False(dashboard.Claude.HasData);
        Assert.False(dashboard.Agy.HasData);
        Assert.Equal("10%", dashboard.Codex.PercentText);
    }

    [Fact]
    public async Task A_refresh_asked_for_during_a_refresh_runs_right_after_it()
    {
        var reads = 0;
        var release = new TaskCompletionSource();
        var dashboard = Dashboard(new UsageSources(
            (_, _) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    release.Task.Wait();
                }

                return null;
            },
            (_, _) => null,
            (_, _) => null));

        var first = dashboard.RefreshAsync();
        await WaitFor(() => reads == 1);
        await dashboard.RefreshAsync(); // returns at once: it is queued
        release.SetResult();
        await first;

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Tick_refreshes_once_a_minute()
    {
        var reads = 0;
        var clock = new Clock(Now);
        var dashboard = Dashboard(new UsageSources((_, _) => { Interlocked.Increment(ref reads); return null; }, (_, _) => null, (_, _) => null), clock);

        dashboard.Tick();
        await WaitFor(() => reads == 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        dashboard.Tick();
        clock.Advance(TimeSpan.FromSeconds(31));
        dashboard.Tick();
        await WaitFor(() => reads == 2);

        Assert.Equal(2, reads);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
