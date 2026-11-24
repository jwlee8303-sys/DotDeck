using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Approvals;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>What the "AI 모니터" toolbar does with a DotDeck session (supplied by the shell).</summary>
/// <param name="OpenTerminal">Shows an interactive session's terminal tab.</param>
/// <param name="ShowOutput">Shows a non-interactive session's output window.</param>
/// <param name="ReviewChanges">Opens the change review of the session.</param>
/// <param name="ConfirmStop">Asks before a running session is stopped.</param>
public sealed record AiMonitorActions(
    Action<AiSession> OpenTerminal,
    Action<AiSession> ShowOutput,
    Func<AiSession, Task> ReviewChanges,
    Func<AiSession, bool> ConfirmStop);

/// <summary>
/// "AI 모니터" panel: the AI sessions of this run (state, guard, scope, elapsed time and whether the session is producing
/// output right now) with their actions: terminal tab, output window, change review, stop and clearing finished rows.
/// Double click opens the session's terminal tab or its output. Claude, Codex or agy started by hand in a plain terminal tab
/// is listed too: the terminals' processes are checked while the panel is visible. Time-based texts are refreshed once
/// a second while the panel is visible.
/// </summary>
public sealed partial class AiMonitorPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "tool.aimonitor";

    /// <summary>
    /// A terminal run counts as ended only after no process of its CLI has been seen in the terminal's job for this
    /// long: launchers, wrappers and self-updates (agy replaces its own executable and starts again) change the PID.
    /// </summary>
    public static readonly TimeSpan ProcessGrace = TimeSpan.FromSeconds(3);

    /// <summary>How often terminals, elapsed and activity texts are refreshed while the panel is visible.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly Action<AiSession> _open;
    private readonly AiMonitorActions? _actions;
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);
    private AiSessionManager? _manager;
    private Func<IEnumerable<TerminalPaneViewModel>>? _terminals;
    private Func<int, (string Name, DateTimeOffset? StartedAt)?>? _inspect;
    private Func<int, int?>? _parentOf;
    private Action<TerminalPaneViewModel>? _openTerminal;
    private ApprovalCoordinator? _approvals;
    private ITimer? _timer;

    /// <param name="open">Double click on a DotDeck session: its terminal tab (interactive) or its output.</param>
    /// <param name="time">Clock (tests).</param>
    /// <param name="actions">Toolbar actions; without them only opening and clearing work.</param>
    public AiMonitorPaneViewModel(Action<AiSession> open, TimeProvider? time = null, AiMonitorActions? actions = null)
        : base(PaneContentId, "AI 모니터", ToolLocation.Right)
    {
        _ui = SynchronizationContext.Current;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _time = time ?? TimeProvider.System;
        _actions = actions;
    }

    /// <summary>Every session of this run, newest first.</summary>
    public ObservableCollection<AiMonitorItemViewModel> Sessions { get; } = [];

    /// <summary>Finished sessions stay in the list; off shows only running ones (rows hide via <see cref="AiMonitorItemViewModel.IsShown"/>).</summary>
    [ObservableProperty]
    private bool _showFinished = true;

    partial void OnShowFinishedChanged(bool value) => Refresh();

    /// <summary>"승인 대기 N건" filter: only sessions waiting for an approval are listed.</summary>
    [ObservableProperty]
    private bool _showOnlyWaiting;

    partial void OnShowOnlyWaitingChanged(bool value) => Refresh();

    /// <summary>Sessions waiting for an approval (the tab title shows the count: "AI 모니터 (2)").</summary>
    public int WaitingCount { get; private set; }

    public bool HasWaiting => WaitingCount > 0;

    public string WaitingSummary => string.Create(CultureInfo.InvariantCulture, $"⚠ 승인 대기 {WaitingCount}건");

    /// <summary>Shows approval requests in the rows (sections) and keeps the order, filter and badge up to date.</summary>
    public void AttachApprovals(ApprovalCoordinator approvals)
    {
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        approvals.Updated += (_, _) => Post(() =>
        {
            // A request from a terminal run the panel has not listed yet (the panel's own scan stops while it is
            // hidden): scan once so the row, the order and the badge include it.
            if (approvals.WaitingItems.Any(i => !Sessions.Any(row => row.ApprovalKey == i.SessionKey)))
            {
                ScanTerminals();
            }

            Refresh();
        });
        Refresh();
    }

    /// <summary>Shows the panel with the session of <paramref name="sessionKey"/> selected (card "열기").</summary>
    public void Reveal(string sessionKey)
    {
        IsVisible = true;
        IsActive = true;
        if (Sessions.FirstOrDefault(s => s.ApprovalKey == sessionKey) is { } item)
        {
            if (!item.IsShown)
            {
                ShowOnlyWaiting = false;
                ShowFinished = true;
            }

            SelectedSession = item;
        }
    }

    [RelayCommand]
    private void ToggleWaitingFilter() => ShowOnlyWaiting = !ShowOnlyWaiting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTerminalCommand), nameof(ShowOutputCommand), nameof(ReviewChangesCommand), nameof(StopCommand))]
    private AiMonitorItemViewModel? _selectedSession;

    partial void OnSelectedSessionChanged(AiMonitorItemViewModel? value) => Context?.Show(value);

    /// <summary>The "컨텍스트" tab of the selected session, or null when not attached (tests).</summary>
    public ContextBudgetViewModel? Context { get; private set; }

    /// <summary>Shows the "컨텍스트" tab below the list; running sessions are analyzed once as they appear.</summary>
    public void AttachContext(ContextBudgetViewModel context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        OnPropertyChanged(nameof(Context));
        context.Show(SelectedSession);
        context.Track(Sessions);
    }

    /// <summary>Keeps every row's "컨텍스트" value up to date, or null when not attached (tests).</summary>
    public ContextUsageTracker? ContextUsageTracker { get; private set; }

    /// <summary>Shows the current context usage on every row (selected or not).</summary>
    public void AttachContextUsage(ContextUsageTracker tracker)
    {
        ContextUsageTracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        tracker.Track(Sessions);
    }

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

    /// <summary>The "사용량" section above the sessions, or null when not attached (tests).</summary>
    public UsageDashboardViewModel? Usage { get; private set; }

    /// <summary>Shows the usage dashboard; it refreshes every minute while the panel is visible.</summary>
    public void AttachUsage(UsageDashboardViewModel usage)
    {
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        OnPropertyChanged(nameof(Usage));
        UpdateTimer();
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
    /// Follows plain terminal tabs: a <c>claude</c>, <c>codex</c> or <c>agy</c> process inside a terminal's job is listed as a row
    /// of that terminal until it ends. <paramref name="inspect"/> reads a process's executable name and start time.
    /// <paramref name="parentOf"/> allows independent runs of the same CLI to be listed separately.
    /// </summary>
    public void AttachTerminals(
        Func<IEnumerable<TerminalPaneViewModel>> terminals,
        Func<int, (string Name, DateTimeOffset? StartedAt)?> inspect,
        Action<TerminalPaneViewModel> open,
        Func<int, int?>? parentOf = null)
    {
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
        _parentOf = parentOf;
        _openTerminal = open ?? throw new ArgumentNullException(nameof(open));
        Tick();
        UpdateTimer();
    }

    /// <summary>The AI CLI an executable name stands for ("claude", "codex.exe", "agy"), or null for anything else.</summary>
    public static CliKind? ClassifyProcess(string? name)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        return stem.Equals("claude", StringComparison.OrdinalIgnoreCase) ? CliKind.Claude
            : stem.Equals("codex", StringComparison.OrdinalIgnoreCase) ? CliKind.Codex
            : stem.Equals("agy", StringComparison.OrdinalIgnoreCase) ? CliKind.Antigravity
            : null;
    }

    /// <summary>Checks the terminals for AI CLIs, then refreshes every row (UI thread; the timer calls it every second).</summary>
    public void Tick()
    {
        ScanTerminals();
        Refresh();
        Usage?.Tick();
        Context?.Tick();
        ContextUsageTracker?.Tick();
    }

    /// <summary>Re-reads every row's state and time-based texts (UI thread).</summary>
    public void Refresh()
    {
        var now = _time.GetUtcNow();

        // A session's terminal tab may have been renamed: the row shows the tab's name.
        var names = _terminals?.Invoke()
            .Where(t => t.Session is not null)
            .GroupBy(t => t.Session!)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);
        foreach (var item in Sessions)
        {
            var name = item.Session is { } session && names is not null && names.TryGetValue(session, out var n) ? n : null;
            if (_approvals is not null && item.Approval is null)
            {
                item.Approval = _approvals.ItemFor(item.ApprovalKey, item.Title);
            }

            item.Refresh(now, ShowFinished, name, ShowOnlyWaiting);
        }

        UpdateApprovalOrder();
        ContextUsageTracker?.Track(Sessions);
        Context?.Track(Sessions);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleSessions));
        OpenTerminalCommand.NotifyCanExecuteChanged();
        ShowOutputCommand.NotifyCanExecuteChanged();
        ReviewChangesCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
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

    /// <summary>"터미널 보기": the terminal tab of an interactive session or of a CLI run by hand.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private void OpenTerminal()
    {
        if (SelectedSession is { Session: { Terminal: not null } session })
        {
            _actions?.OpenTerminal(session);
        }
        else if (SelectedSession is { TerminalPane: { } terminal })
        {
            _openTerminal?.Invoke(terminal);
        }
    }

    private bool CanOpenTerminal() => SelectedSession is { HasTerminal: true };

    /// <summary>"출력 보기": the output window of a non-interactive session.</summary>
    [RelayCommand(CanExecute = nameof(CanShowOutput))]
    private void ShowOutput()
    {
        if (SelectedSession is { Session: { Terminal: null } session })
        {
            _actions?.ShowOutput(session);
        }
    }

    private bool CanShowOutput() => _actions is not null && SelectedSession is { Session.Terminal: null };

    /// <summary>"변경 검토": files changed in the session's scope since it started (DotDeck sessions only).</summary>
    [RelayCommand(CanExecute = nameof(CanReviewChanges))]
    private Task ReviewChanges() =>
        SelectedSession is { Session: { } session } && _actions is not null ? _actions.ReviewChanges(session) : Task.CompletedTask;

    private bool CanReviewChanges() => _actions is not null && SelectedSession is { Session: not null };

    /// <summary>"중지": stops a running DotDeck session after asking (the whole process tree ends).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (SelectedSession is { Session: { State: AiSessionState.Running } session }
            && _manager is not null && _actions is not null && _actions.ConfirmStop(session))
        {
            _manager.Stop(session);
        }
    }

    private bool CanStop() => _actions is not null && SelectedSession is { Session.State: AiSessionState.Running };

    /// <summary>Removes finished rows from the list (a session's folder stays until cleanup).</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Sessions.Where(s => !s.IsRunning).ToList())
        {
            if (item.Session is { } session)
            {
                _manager?.MarkClosed(session);
                _cleared.Add(session.Id);
            }

            item.Detach();
            Context?.Forget(item);
            ContextUsageTracker?.Forget(item);
            Sessions.Remove(item);
        }

        if (SelectedSession is not null && !Sessions.Contains(SelectedSession))
        {
            SelectedSession = null;
        }

        Refresh();
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
            if (!_cleared.Contains(session.Id) && Sessions.All(s => s.Session != session))
            {
                Sessions.Insert(0, new AiMonitorItemViewModel(session, _time));
            }
        }

        Refresh();
    }

    // Match surviving processes before using the grace period to bridge a launcher or self-update replacement.
    private void ScanTerminals()
    {
        if (_terminals is null || _inspect is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var found = new Dictionary<(TerminalPaneViewModel Pane, CliKind Cli), List<HashSet<(int Id, DateTimeOffset? StartedAt)>>>();
        foreach (var pane in _terminals())
        {
            if (pane.IsAiSession || !pane.IsRunning)
            {
                continue; // an AI session tab is listed through its session
            }

            var jobIds = pane.Connection.JobProcessIds().ToHashSet();
            var byCli = new Dictionary<CliKind, Dictionary<int, DateTimeOffset?>>();
            foreach (var id in jobIds)
            {
                if (_inspect(id) is not { } info || ClassifyProcess(info.Name) is not { } cli)
                {
                    continue;
                }

                if (!byCli.TryGetValue(cli, out var processes))
                {
                    processes = [];
                    byCli.Add(cli, processes);
                }

                processes.Add(id, info.StartedAt);
            }

            foreach (var (cli, processes) in byCli)
            {
                found.Add((pane, cli), GroupProcesses(processes, jobIds));
            }
        }

        var unmatched = new List<AiMonitorItemViewModel>();
        foreach (var item in Sessions)
        {
            if (item.TerminalPane is null || !item.IsRunning)
            {
                continue;
            }

            if (!Match(item, requireOverlap: true))
            {
                unmatched.Add(item);
            }
        }

        foreach (var item in unmatched)
        {
            if (now - (item.LastSeenAlive ?? item.StartedAt) >= ProcessGrace)
            {
                item.MarkEnded();
            }
            else
            {
                Match(item, requireOverlap: false);
            }
        }

        foreach (var ((pane, cli), groups) in found)
        {
            foreach (var processes in groups)
            {
                var started = processes.Min(process => process.StartedAt ?? now);
                Sessions.Insert(0, new AiMonitorItemViewModel(pane, cli, started, _time) { TerminalProcesses = processes, LastSeenAlive = now });
            }
        }

        bool Match(AiMonitorItemViewModel item, bool requireOverlap)
        {
            if (!found.TryGetValue((item.TerminalPane!, item.Cli), out var groups))
            {
                return false;
            }

            var index = requireOverlap
                ? groups.FindIndex(group => item.TerminalProcesses?.Overlaps(group) == true)
                : groups.Count > 0 ? 0 : -1;
            if (index < 0)
            {
                return false;
            }

            item.TerminalProcesses = groups[index];
            item.LastSeenAlive = now;
            groups.RemoveAt(index);
            return true;
        }
    }

    private List<HashSet<(int Id, DateTimeOffset? StartedAt)>> GroupProcesses(
        Dictionary<int, DateTimeOffset?> processes, HashSet<int> jobIds)
    {
        if (_parentOf is not { } parentOf || processes.Count == 1)
        {
            return [processes.Select(p => (p.Key, p.Value)).ToHashSet()];
        }

        var groups = new Dictionary<int, HashSet<(int Id, DateTimeOffset? StartedAt)>>();
        foreach (var (id, started) in processes)
        {
            var root = id;
            var rootStarted = started;
            var visited = new HashSet<int> { id };
            for (var child = id; parentOf(child) is { } parent && jobIds.Contains(parent) && visited.Add(parent); child = parent)
            {
                if (processes.TryGetValue(parent, out var parentStarted))
                {
                    // Windows can reuse a departed parent's PID.
                    if (rootStarted is { } lower && parentStarted is { } upper && upper >= lower)
                    {
                        break;
                    }

                    root = parent;
                    rootStarted = parentStarted;
                }
            }

            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups.Add(root, group);
            }

            group.Add((id, started));
        }

        return [.. groups.Values];
    }

    // Waiting sessions first (oldest request first) when the setting is on; the badge counts them.
    private void UpdateApprovalOrder()
    {
        var waiting = Sessions.Where(s => s.IsAwaitingApproval).ToList();
        WaitingCount = waiting.Count;
        Title = WaitingCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"AI 모니터 ({WaitingCount})") : "AI 모니터";
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(WaitingSummary));
        if (_approvals?.Service.Settings.PinWaitingToTop != true || waiting.Count == 0)
        {
            return;
        }

        var ordered = waiting.OrderBy(s => s.Approval?.Request?.RequestedAt ?? DateTimeOffset.MaxValue).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Sessions.IndexOf(ordered[i]);
            if (from != i)
            {
                Sessions.Move(from, i);
            }
        }
    }

    // The timer runs only while the panel is shown: a hidden panel costs nothing.
    private void UpdateTimer()
    {
        if (IsVisible && (_manager is not null || _terminals is not null || Usage is not null))
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

/// <summary>One row of the "AI 모니터" panel: a DotDeck AI session, or Claude/Codex/agy run by hand in a terminal tab.</summary>
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
    private string? _terminalName;

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

    internal HashSet<(int Id, DateTimeOffset? StartedAt)>? TerminalProcesses { get; set; }

    /// <summary>Last scan that found the CLI in the terminal (terminal rows; see <see cref="AiMonitorPaneViewModel.ProcessGrace"/>).</summary>
    internal DateTimeOffset? LastSeenAlive { get; set; }

    public CliKind Cli { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Key of the row's approvals: the DotDeck session ID, or the terminal tab + CLI of a hand run.</summary>
    public string ApprovalKey => Session?.Id ?? ApprovalCoordinator.TerminalKey(TerminalPane!, Cli);

    /// <summary>The row's approval area (current request or last result); null before approvals are attached.</summary>
    [ObservableProperty]
    private ApprovalItemViewModel? _approval;

    public bool IsAwaitingApproval => IsRunning && Approval?.IsWaiting == true;

    /// <summary>"Claude", "Codex" or "agy" (label of the section).</summary>
    public string CliLabel => CliName;

    /// <summary>The session started without DotDeck's tool guard ("AI 세션 훅" setting at launch).</summary>
    public bool IsGuardOff => Session is { GuardEnabled: false };

    private string CliName => Cli switch
    {
        CliKind.Claude => "Claude",
        CliKind.Codex => "Codex",
        _ => "agy",
    };

    /// <summary>The session's terminal tab name (tabs can be renamed), else the session title; "Claude · 탭 이름" for a hand run.</summary>
    public string Title => Session is { } session ? _terminalName ?? session.Title : CliName + " · " + TerminalPane!.DisplayName;

    /// <summary>The row has a terminal tab to show (an interactive session, or a CLI run by hand).</summary>
    public bool HasTerminal => TerminalPane is not null || Session?.Terminal is not null;

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

    /// <summary>The session's current context usage (null until first read; the last value stays after the session ends).</summary>
    public ContextUsage? ContextUsage { get; private set; }

    /// <summary>"컨텍스트 38%", "컨텍스트 38% (추정)", "컨텍스트 4.2만 토큰", "컨텍스트 첫 응답 후 측정", …; empty before the first read.</summary>
    public string ContextText => ContextUsage is { } usage ? "컨텍스트 " + usage.Text : string.Empty;

    internal bool ContextTracked { get; set; }

    /// <summary>
    /// The session log the row's context value was matched to (set by <see cref="ContextUsageTracker"/>); the
    /// "컨텍스트" tab reads the same file so both describe one conversation.
    /// </summary>
    public string? ContextLogPath
    {
        get;
        internal set => SetProperty(ref field, value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Orange/red from the settings' thresholds; never colored without a percentage.</summary>
    public ContextUsageLevel ContextLevel { get; private set; }

    /// <summary>Where the value came from ("Codex 로그 실측", "agy 제공", "Claude: 모델 기준 추정(창 200k)", …).</summary>
    public string? ContextToolTip => ContextUsage?.ToolTip;

    internal void ShowContext(ContextUsage usage, ContextBudgetSettings settings)
    {
        ContextUsage = usage;
        ContextLevel = usage.Level(settings);
        OnPropertyChanged(nameof(ContextUsage));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(ContextLevel));
        OnPropertyChanged(nameof(ContextToolTip));
    }

    /// <summary>The row is listed (running, or finished while finished sessions are shown).</summary>
    public bool IsShown { get; private set; } = true;

    /// <summary>The terminal run's CLI is gone (terminal rows only; a session's end comes from the session).</summary>
    internal void MarkEnded()
    {
        if (Session is null)
        {
            _processEnded = true;
            TerminalProcesses = null;
        }
    }

    internal void Refresh(DateTimeOffset now, bool showFinished, string? terminalName = null, bool onlyWaiting = false)
    {
        _now = now;
        _terminalName = terminalName ?? _terminalName;
        IsShown = (showFinished || IsRunning) && (!onlyWaiting || IsAwaitingApproval);
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

    /// <summary>Stops following the output (the row was cleared).</summary>
    internal void Detach() => Unwatch();

    // Only the time of the latest output is kept; the text itself is shown by the terminal tab or the output window.
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
