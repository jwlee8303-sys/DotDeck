using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.Core.Logging;
using DotDeck.Core.Usage;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>Where the dashboard reads from (App: the real readers; tests: fakes). Each source may throw.</summary>
public sealed record UsageSources(
    Func<DateTimeOffset, TimeZoneInfo, TokenUsageReport?> Claude,
    Func<DateTimeOffset, TimeZoneInfo, TokenUsageReport?> Codex,
    Func<DateTimeOffset, TimeZoneInfo, AgyActivity?> Agy,
    ClaudeStatusLineInstaller? StatusLine = null);

/// <summary>
/// "사용량" section at the top of the AI 모니터: today's tokens of Claude and Codex (cache reads apart), the 5-hour
/// limit the provider's server reported with when it was last seen, the busiest session, tokens per hour, and agy's
/// conversation activity. Refreshed every minute while the monitor is visible; a source that fails shows "데이터 없음"
/// without affecting the others.
/// </summary>
public sealed partial class UsageDashboardViewModel : ObservableObject
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly UsageSources _sources;
    private readonly Func<string?, string> _projectName;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly IDialogService? _dialogs;
    private readonly IAppLog? _log;
    private DateTimeOffset? _lastRefresh;
    private bool _refreshing;
    private bool _refreshAgain;

    /// <param name="projectName">Display name for a session's working folder (DotDeck project name or folder name).</param>
    public UsageDashboardViewModel(
        UsageSources sources,
        Func<string?, string> projectName,
        TimeProvider? time = null,
        TimeZoneInfo? zone = null,
        IDialogService? dialogs = null,
        IAppLog? log = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _projectName = projectName ?? throw new ArgumentNullException(nameof(projectName));
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _dialogs = dialogs;
        _log = log;
    }

    public UsageCardViewModel Claude { get; } = new("Claude");

    public UsageCardViewModel Codex { get; } = new("Codex");

    public UsageCardViewModel Agy { get; } = new("agy");

    public bool CanManageStatusLine => _sources.StatusLine is not null;

    /// <summary>Claude Code's status line runs DotDeck's script (null: not known yet or unreadable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallStatusLine), nameof(CanUninstallStatusLine))]
    private bool? _isStatusLineInstalled;

    public bool CanInstallStatusLine => CanManageStatusLine && IsStatusLineInstalled == false;

    public bool CanUninstallStatusLine => CanManageStatusLine && IsStatusLineInstalled == true;

    /// <summary>"갱신 14:32" after the first refresh.</summary>
    [ObservableProperty]
    private string _refreshedText = string.Empty;

    /// <summary>Called every second by the monitor while it is visible: refreshes when a minute has passed.</summary>
    public void Tick()
    {
        if (_lastRefresh is not { } last || _time.GetUtcNow() - last >= RefreshInterval)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Reads all sources off the UI thread and updates the cards. One refresh runs at a time: a request made meanwhile
    /// runs once more right after it, so a change made during a refresh (status line connected) is never missed.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshOnceAsync().ConfigureAwait(true);
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        var now = _time.GetUtcNow();
        _lastRefresh = now;
        var (claude, codex, agy, installed) = await Task.Run(() => (
            Try("Claude", () => _sources.Claude(now, _zone)),
            Try("Codex", () => _sources.Codex(now, _zone)),
            Try("agy", () => _sources.Agy(now, _zone)),
            _sources.StatusLine is { } statusLine ? Try("Claude 상태 표시줄", () => (bool?)statusLine.IsInstalled()) : null)).ConfigureAwait(true);
        Claude.ShowTokens(claude, now, _zone, _projectName, claude?.Limits is null && CanManageStatusLine
            ? "5시간 한도: 상태 표시줄 연결 후 Claude Code를 사용하면 표시"
            : null);
        Codex.ShowTokens(codex, now, _zone, _projectName, null);
        Agy.ShowActivity(agy, now, _zone);
        IsStatusLineInstalled = installed;
        RefreshedText = "갱신 " + TimeOf(now, _zone);
    }

    [RelayCommand]
    private async Task InstallStatusLineAsync()
    {
        if (_sources.StatusLine is not { } statusLine)
        {
            return;
        }

        if (_dialogs is not null && !_dialogs.Confirm("Claude 사용률 연결",
                "Claude Code 상태 표시줄에 DotDeck 스크립트를 연결합니다.\n\n"
                + $"- {statusLine.SettingsPath}의 statusLine을 바꿉니다(저장 전 백업).\n"
                + "- 기존 상태 표시줄 명령은 DotDeck 스크립트 뒤에 파이프로 이어서 그대로 실행됩니다.\n"
                + $"- 사용률과 리셋 시각은 {Path.GetDirectoryName(statusLine.ScriptPath)}에 저장됩니다.\n"
                + "- settings.json은 다시 정렬되어 저장되며, 파일 안의 주석은 지워집니다(원본은 백업에 남음).\n"
                + "- Windows PowerShell이 필요하고, 값은 Claude Code가 다음에 응답할 때부터 들어옵니다.\n\n연결할까요?"))
        {
            return;
        }

        await ChangeStatusLineAsync(statusLine.Install, "연결").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UninstallStatusLineAsync()
    {
        if (_sources.StatusLine is not { } statusLine)
        {
            return;
        }

        if (_dialogs is not null && !_dialogs.Confirm("Claude 사용률 연결 해제",
                "Claude Code 상태 표시줄에서 DotDeck 스크립트를 빼고(이어서 실행하던 명령은 현재 내용 그대로 유지), "
                + "DotDeck 스크립트와 저장된 사용률을 지웁니다. 계속할까요?"))
        {
            return;
        }

        await ChangeStatusLineAsync(statusLine.Uninstall, "연결 해제").ConfigureAwait(true);
    }

    private async Task ChangeStatusLineAsync(Action change, string what)
    {
        try
        {
            await Task.Run(change).ConfigureAwait(true);
            _log?.Info($"Claude 상태 표시줄 {what}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                       or InvalidOperationException or DotDeck.Core.Documents.ExternalChangeException)
        {
            _log?.Warning($"Claude 상태 표시줄 {what} 실패", ex);
            _dialogs?.ShowError("Claude 사용률 " + what, $"Claude 설정을 바꾸지 못했습니다: {ex.Message}");
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private T? Try<T>(string what, Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException
                                       or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            // ArgumentException covers out-of-range times and dates: one source's bad data never stops the others.
            _log?.Warning($"{what} 사용량을 읽지 못했습니다.", ex);
            return null;
        }
    }

    private bool? Try(string what, Func<bool?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                       or InvalidOperationException or ArgumentException)
        {
            _log?.Warning($"{what} 설정을 읽지 못했습니다.", ex);
            return null;
        }
    }

    /// <summary>"14:32" today, "11/22 14:32" on another day.</summary>
    internal static string TimeOf(DateTimeOffset time, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now ?? time, zone).Date;
        return local.ToString(local.Date == today ? "HH:mm" : "MM/dd HH:mm", CultureInfo.InvariantCulture);
    }
}

public enum UsageLevel
{
    Normal,

    /// <summary>70 % or more of the 5-hour limit.</summary>
    Warning,

    /// <summary>90 % or more of the 5-hour limit.</summary>
    Critical,
}

/// <summary>One bar of the hourly graph (height in pixels, at most <see cref="UsageCardViewModel.GraphHeight"/>).</summary>
public sealed record UsageBar(double Height, string ToolTip);

/// <summary>One AI in the usage dashboard.</summary>
public sealed partial class UsageCardViewModel(string name) : ObservableObject
{
    public const double GraphHeight = 24;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _hasData;

    /// <summary>"오늘 1.23M 토큰 · 캐시 읽기 4.5M", or agy's "오늘 대화 3개".</summary>
    [ObservableProperty]
    private string _todayText = string.Empty;

    /// <summary>"42%" of the 5-hour limit, empty when unknown.</summary>
    [ObservableProperty]
    private string _percentText = string.Empty;

    /// <summary>"5시간 한도 42% · 리셋 15:00 · 마지막 갱신 14:32", empty when unknown.</summary>
    [ObservableProperty]
    private string _limitText = string.Empty;

    [ObservableProperty]
    private UsageLevel _level;

    /// <summary>"최다 세션: DotDeck (1.2M)", or agy's last use.</summary>
    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private IReadOnlyList<UsageBar> _bars = [];

    public bool HasBars => Bars.Count > 0;

    partial void OnBarsChanged(IReadOnlyList<UsageBar> value) => OnPropertyChanged(nameof(HasBars));

    internal void ShowTokens(TokenUsageReport? report, DateTimeOffset now, TimeZoneInfo zone, Func<string?, string> projectName, string? missingLimitNote)
    {
        if (report is null)
        {
            ShowNoData();
            return;
        }

        var today = report.Today;
        HasData = true;
        TodayText = $"오늘 {Tokens(today.Total)} 토큰 · 캐시 읽기 {Tokens(today.CacheRead)}";
        DetailText = today.SessionCount == 0
            ? "오늘 사용 기록 없음"
            : $"최다 세션: {projectName(today.TopSessionProject)} ({Tokens(today.TopSessionTotal)}) · 세션 {today.SessionCount}개";
        var max = today.Hourly.Count == 0 ? 0 : today.Hourly.Max();
        Bars = today.SessionCount == 0
            ? []
            : [.. today.Hourly.Select((v, hour) => new UsageBar(max == 0 ? 0 : Math.Max(v > 0 ? 1 : 0, GraphHeight * v / max),
                string.Create(CultureInfo.InvariantCulture, $"{hour}시 · {Tokens(v)} 토큰")))];

        if (report.Limits is { FiveHour: { } five } limits)
        {
            var used = five.UsedPercentAt(now);
            var reset = five.ResetsAt is { } at
                ? at <= now ? " · 리셋됨" : " · 리셋 " + UsageDashboardViewModel.TimeOf(at, zone, now)
                : string.Empty;
            PercentText = string.Create(CultureInfo.InvariantCulture, $"{used:0}%");
            LimitText = string.Create(CultureInfo.InvariantCulture, $"5시간 한도 {used:0}%{reset} · 마지막 갱신 {UsageDashboardViewModel.TimeOf(limits.MeasuredAt, zone, now)}");
            Level = LevelOf(used);
            Note = null;
        }
        else
        {
            PercentText = string.Empty;
            LimitText = string.Empty;
            Level = UsageLevel.Normal;
            Note = missingLimitNote;
        }
    }

    internal void ShowActivity(AgyActivity? activity, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (activity is null)
        {
            ShowNoData();
            return;
        }

        HasData = true;
        TodayText = $"오늘 대화 {activity.ConversationsToday}개";
        DetailText = activity.LastUsedAt is { } last ? "마지막 사용 " + UsageDashboardViewModel.TimeOf(last, zone, now) : "사용 기록 없음";
        PercentText = string.Empty;
        LimitText = string.Empty;
        Level = UsageLevel.Normal;
        Bars = [];
        Note = "토큰·한도 정보는 agy 로그에서 읽을 수 없습니다.";
    }

    private void ShowNoData()
    {
        HasData = false;
        TodayText = DetailText = PercentText = LimitText = string.Empty;
        Note = null;
        Level = UsageLevel.Normal;
        Bars = [];
    }

    public static UsageLevel LevelOf(double percent) => percent >= 90 ? UsageLevel.Critical : percent >= 70 ? UsageLevel.Warning : UsageLevel.Normal;

    /// <summary>"950", "12.3K", "1.23M".</summary>
    public static string Tokens(long value) => value switch
    {
        < 1_000 => value.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        < 1_000_000_000 => (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        _ => (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
    };
}
