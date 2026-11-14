using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// "AI 모니터" panel: a compact view of the AI sessions of this run (state, guard, scope, elapsed time and whether the
/// session is producing output right now). Double click opens the session's terminal tab or its "AI 작업" entry.
/// Time-based texts are refreshed once a second while the panel is visible.
/// </summary>
public sealed partial class AiMonitorPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "tool.aimonitor";

    /// <summary>How often elapsed and activity texts are refreshed while the panel is visible.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly Action<AiSession> _open;
    private AiSessionManager? _manager;
    private ITimer? _timer;

    public AiMonitorPaneViewModel(Action<AiSession> open, TimeProvider? time = null)
        : base(PaneContentId, "AI 모니터", ToolLocation.Right)
    {
        _ui = SynchronizationContext.Current;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Every session of this run, newest first.</summary>
    public ObservableCollection<AiMonitorItemViewModel> Sessions { get; } = [];

    /// <summary>Finished sessions stay in the list; off shows only running ones (rows hide via <see cref="AiMonitorItemViewModel.IsShown"/>).</summary>
    [ObservableProperty]
    private bool _showFinished = true;

    partial void OnShowFinishedChanged(bool value) => Refresh();

    [ObservableProperty]
    private AiMonitorItemViewModel? _selectedSession;

    public bool HasVisibleSessions => Sessions.Any(s => s.IsShown);

    /// <summary>"실행 중 2 · 작업 중 1 · 끝남 3".</summary>
    public string Summary
    {
        get
        {
            var running = Sessions.Count(s => s.IsRunning);
            var busy = Sessions.Count(s => s.IsBusy);
            var finished = Sessions.Count - running;
            return string.Create(CultureInfo.InvariantCulture, $"실행 중 {running} · 작업 중 {busy} · 끝남 {finished}");
        }
    }

    /// <summary>Follows the manager: new sessions appear as they start.</summary>
    public void Attach(AiSessionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        manager.Changed += (_, _) => Post(Sync);
        Sync();
        UpdateTimer();
    }

    /// <summary>Re-reads every row's state and time-based texts (UI thread; the timer calls it every second).</summary>
    public void Refresh()
    {
        var now = _time.GetUtcNow();
        foreach (var item in Sessions)
        {
            item.Refresh(now, ShowFinished);
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleSessions));
    }

    [RelayCommand]
    private void Open(AiMonitorItemViewModel? item)
    {
        if ((item ?? SelectedSession) is { } target)
        {
            _open(target.Session);
        }
    }

    protected override void OnVisibilityChanged(bool isVisible)
    {
        UpdateTimer();
        if (isVisible)
        {
            Refresh();
        }
    }

    private void Sync()
    {
        if (_manager is null)
        {
            return;
        }

        foreach (var session in _manager.Sessions)
        {
            if (Sessions.All(s => s.Session != session))
            {
                Sessions.Insert(0, new AiMonitorItemViewModel(session, _time));
            }
        }

        Refresh();
    }

    // The timer runs only while the panel is shown: a hidden panel costs nothing.
    private void UpdateTimer()
    {
        if (IsVisible && _manager is not null)
        {
            _timer ??= _time.CreateTimer(_ => Post(Refresh), null, RefreshInterval, RefreshInterval);
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}

/// <summary>One session row of the "AI 모니터" panel.</summary>
public sealed partial class AiMonitorItemViewModel : ObservableObject
{
    /// <summary>Output within this window counts as "작업 중".</summary>
    public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private long _lastOutputTicks; // UTC ticks, written by output reader threads
    private TerminalConnection? _watchedTerminal;
    private bool _watchingOutput;
    private DateTimeOffset? _endedAt;
    private DateTimeOffset _now;

    internal AiMonitorItemViewModel(AiSession session, TimeProvider time)
    {
        Session = session;
        _time = time;
        _now = time.GetUtcNow();
        Watch();
    }

    public AiSession Session { get; }

    public string Title => Session.Title;

    public string KindText => (Session.Cli == CliKind.Claude ? "Claude" : "Codex")
        + (Session.Mode == LaunchMode.Interactive ? " · 대화형" : " · 자동");

    /// <summary>Projects in the scope, primary first.</summary>
    public string ScopeText => string.Join(", ", Session.Scope.Projects.Select(p => p.Name));

    public string StartedText => Session.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public bool IsRunning => Session.State == AiSessionState.Running;

    public bool IsFailed => Session.State == AiSessionState.Failed;

    public string StateText => Session.State switch
    {
        AiSessionState.Running => "실행 중",
        AiSessionState.Exited => "완료",
        AiSessionState.Stopped => "중지됨",
        _ => Session.ExitCode is { } code ? $"실패 ({code})" : "실패",
    };

    public string GuardText => Session.Cli == CliKind.Codex
        ? "Codex sandbox"
        : Session.GuardConnected ? "가드 적용됨" : "가드 확인 대기";

    public string? Message => Session.Message;

    /// <summary>Running and printed something within <see cref="BusyWindow"/>.</summary>
    public bool IsBusy => IsRunning && LastOutput is { } last && _now - last < BusyWindow;

    /// <summary>"작업 중", "대기 · 2분 전 출력", "출력 없음", or the end state for a finished session.</summary>
    public string ActivityText
    {
        get
        {
            if (!IsRunning)
            {
                return StateText;
            }

            if (LastOutput is not { } last)
            {
                return "출력 없음";
            }

            return IsBusy ? "작업 중" : "대기 · " + FormatSpan(_now - last) + " 전 출력";
        }
    }

    /// <summary>Running time so far, or until the session ended.</summary>
    public string ElapsedText => FormatSpan((_endedAt ?? _now) - Session.StartedAt);

    private DateTimeOffset? LastOutput
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastOutputTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>The row is listed (running, or finished while finished sessions are shown).</summary>
    public bool IsShown { get; private set; } = true;

    internal void Refresh(DateTimeOffset now, bool showFinished)
    {
        _now = now;
        IsShown = showFinished || IsRunning;
        if (!IsRunning)
        {
            _endedAt ??= now;
            Unwatch();
        }
        else
        {
            Watch(); // the terminal may be attached after the session object was announced
        }

        OnPropertyChanged(string.Empty);
    }

    internal static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}시간 {span.Minutes}분")
            : span.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}분 {span.Seconds}초")
                : string.Create(CultureInfo.InvariantCulture, $"{span.Seconds}초");
    }

    // Only the time of the latest output is kept; the text itself is shown by the terminal tab or the AI 작업 panel.
    private void Watch()
    {
        if (!IsRunning)
        {
            return;
        }

        if (Session.Terminal is { } terminal)
        {
            if (_watchedTerminal is null)
            {
                _watchedTerminal = terminal;
                terminal.Attach(OnTerminalOutput);
            }
        }
        else if (!_watchingOutput)
        {
            _watchingOutput = true;
            if (Session.AttachOutput(OnSessionOutput).Count > 0)
            {
                MarkOutput();
            }
        }
    }

    private void Unwatch()
    {
        _watchedTerminal?.Detach(OnTerminalOutput);
        _watchedTerminal = null;
        if (_watchingOutput)
        {
            Session.DetachOutput(OnSessionOutput);
            _watchingOutput = false;
        }
    }

    private void OnTerminalOutput(string text) => MarkOutput();

    private void OnSessionOutput(AiSession session, string line, bool isError) => MarkOutput();

    private void MarkOutput() => Interlocked.Exchange(ref _lastOutputTicks, _time.GetUtcNow().UtcTicks);
}
