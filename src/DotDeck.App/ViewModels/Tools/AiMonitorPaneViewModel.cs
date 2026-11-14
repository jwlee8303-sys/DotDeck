using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// "AI 모니터" panel: a compact view of the AI sessions of this run (state, guard, scope, elapsed time and whether the
/// session is producing output right now). Double click opens the session's terminal tab or its "AI 작업" entry.
/// Claude or Codex started by hand in a plain terminal tab is listed too: the terminals' processes are checked while
/// the panel is visible. Time-based texts are refreshed once a second while the panel is visible.
/// </summary>
public sealed partial class AiMonitorPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "tool.aimonitor";

    /// <summary>How often terminals, elapsed and activity texts are refreshed while the panel is visible.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly Action<AiSession> _open;
    private AiSessionManager? _manager;
    private Func<IEnumerable<TerminalPaneViewModel>>? _terminals;
    private Func<int, (string Name, DateTimeOffset? StartedAt)?>? _inspect;
    private Action<TerminalPaneViewModel>? _openTerminal;
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

    /// <summary>
    /// Follows plain terminal tabs: a <c>claude</c> or <c>codex</c> process inside a terminal's job is listed as a row
    /// of that terminal until it ends. <paramref name="inspect"/> reads a process's executable name and start time.
    /// </summary>
    public void AttachTerminals(
        Func<IEnumerable<TerminalPaneViewModel>> terminals,
        Func<int, (string Name, DateTimeOffset? StartedAt)?> inspect,
        Action<TerminalPaneViewModel> open)
    {
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
        _openTerminal = open ?? throw new ArgumentNullException(nameof(open));
        Tick();
        UpdateTimer();
    }

    /// <summary>The AI CLI an executable name stands for ("claude", "codex.exe"), or null for anything else.</summary>
    public static CliKind? ClassifyProcess(string? name)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        return stem.Equals("claude", StringComparison.OrdinalIgnoreCase) ? CliKind.Claude
            : stem.Equals("codex", StringComparison.OrdinalIgnoreCase) ? CliKind.Codex
            : null;
    }

    /// <summary>Checks the terminals for AI CLIs, then refreshes every row (UI thread; the timer calls it every second).</summary>
    public void Tick()
    {
        ScanTerminals();
        Refresh();
    }

    /// <summary>Re-reads every row's state and time-based texts (UI thread).</summary>
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
        if ((item ?? SelectedSession) is not { } target)
        {
            return;
        }

        if (target.Session is { } session)
        {
            _open(session);
        }
        else if (target.TerminalPane is { } terminal)
        {
            _openTerminal?.Invoke(terminal);
        }
    }

    protected override void OnVisibilityChanged(bool isVisible)
    {
        UpdateTimer();
        if (isVisible)
        {
            Tick();
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

    // One row per terminal and CLI while it runs (the CLI's own helper processes add no rows); a row whose CLI is gone,
    // or whose terminal ended or was closed, is marked ended. A CLI started again later gets a new row.
    private void ScanTerminals()
    {
        if (_terminals is null || _inspect is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var found = new Dictionary<(TerminalPaneViewModel Pane, CliKind Cli), DateTimeOffset>();
        foreach (var pane in _terminals())
        {
            if (pane.IsAiSession || !pane.IsRunning)
            {
                continue; // an AI session tab is listed through its session
            }

            foreach (var id in pane.Connection.JobProcessIds())
            {
                if (_inspect(id) is not { } info || ClassifyProcess(info.Name) is not { } cli)
                {
                    continue;
                }

                var started = info.StartedAt ?? now;
                found[(pane, cli)] = found.TryGetValue((pane, cli), out var earlier) && earlier < started ? earlier : started;
            }
        }

        foreach (var item in Sessions)
        {
            if (item.TerminalPane is { } pane && item.IsRunning && !found.Remove((pane, item.Cli)))
            {
                item.MarkEnded();
            }
        }

        foreach (var ((pane, cli), started) in found)
        {
            Sessions.Insert(0, new AiMonitorItemViewModel(pane, cli, started, _time));
        }
    }

    // The timer runs only while the panel is shown: a hidden panel costs nothing.
    private void UpdateTimer()
    {
        if (IsVisible && (_manager is not null || _terminals is not null))
        {
            _timer ??= _time.CreateTimer(_ => Post(Tick), null, RefreshInterval, RefreshInterval);
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

/// <summary>One row of the "AI 모니터" panel: a DotDeck AI session, or Claude/Codex run by hand in a terminal tab.</summary>
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
    private bool _processEnded;

    internal AiMonitorItemViewModel(AiSession session, TimeProvider time)
    {
        Session = session;
        Cli = session.Cli;
        StartedAt = session.StartedAt;
        _time = time;
        _now = time.GetUtcNow();
        Watch();
    }

    internal AiMonitorItemViewModel(TerminalPaneViewModel terminal, CliKind cli, DateTimeOffset startedAt, TimeProvider time)
    {
        TerminalPane = terminal;
        Cli = cli;
        StartedAt = startedAt;
        _time = time;
        _now = time.GetUtcNow();
        Watch();
    }

    /// <summary>The DotDeck session of the row, or null for a CLI run by hand in <see cref="TerminalPane"/>.</summary>
    public AiSession? Session { get; }

    /// <summary>The plain terminal tab the CLI runs in, or null for a DotDeck session.</summary>
    public TerminalPaneViewModel? TerminalPane { get; }

    public CliKind Cli { get; }

    public DateTimeOffset StartedAt { get; }

    private string CliName => Cli == CliKind.Claude ? "Claude" : "Codex";

    public string Title => Session?.Title ?? CliName + " · " + TerminalPane!.DisplayName;

    public string KindText => CliName + (Session is null ? " · 터미널" : Session.Mode == LaunchMode.Interactive ? " · 대화형" : " · 자동");

    /// <summary>Projects in the scope, primary first; for a terminal run, the terminal's current folder.</summary>
    public string ScopeText => Session is { } session
        ? string.Join(", ", session.Scope.Projects.Select(p => p.Name))
        : TerminalPane!.Connection.WorkingDirectory ?? string.Empty;

    public string StartedText => StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public bool IsRunning => Session is { } session ? session.State == AiSessionState.Running : !_processEnded;

    public bool IsFailed => Session?.State == AiSessionState.Failed;

    public string StateText => Session is not { } session
        ? (_processEnded ? "종료됨" : "실행 중")
        : session.State switch
        {
            AiSessionState.Running => "실행 중",
            AiSessionState.Exited => "완료",
            AiSessionState.Stopped => "중지됨",
            _ => session.ExitCode is { } code ? $"실패 ({code})" : "실패",
        };

    /// <summary>A CLI started by hand runs without DotDeck's session guard and scope.</summary>
    public string GuardText => Session is not { } session
        ? "직접 실행 · 가드 없음"
        : session.Cli == CliKind.Codex
            ? "Codex sandbox"
            : session.GuardConnected ? "가드 적용됨" : "가드 확인 대기";

    public string? Message => Session?.Message;

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
    public string ElapsedText => FormatSpan((_endedAt ?? _now) - StartedAt);

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

    /// <summary>The terminal run's CLI is gone (terminal rows only; a session's end comes from the session).</summary>
    internal void MarkEnded()
    {
        if (Session is null)
        {
            _processEnded = true;
        }
    }

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

        if ((Session?.Terminal ?? TerminalPane?.Connection) is { } terminal)
        {
            if (_watchedTerminal is null)
            {
                _watchedTerminal = terminal;
                terminal.Attach(OnTerminalOutput);
            }
        }
        else if (!_watchingOutput && Session is { } session)
        {
            _watchingOutput = true;
            if (session.AttachOutput(OnSessionOutput).Count > 0)
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
            Session?.DetachOutput(OnSessionOutput);
            _watchingOutput = false;
        }
    }

    private void OnTerminalOutput(string text) => MarkOutput();

    private void OnSessionOutput(AiSession session, string line, bool isError) => MarkOutput();

    private void MarkOutput() => Interlocked.Exchange(ref _lastOutputTicks, _time.GetUtcNow().UtcTicks);
}
