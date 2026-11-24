using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Approvals;

/// <summary>What the coordinator needs from the shell and the windows (supplied at startup).</summary>
/// <param name="Terminals">Open terminal tabs (AI sessions and plain terminals).</param>
/// <param name="ShowTerminal">Shows a terminal tab.</param>
/// <param name="ShowAiTerminal">Shows the terminal tab of an interactive DotDeck session.</param>
/// <param name="RevealInMonitor">Shows AI 모니터 with the session of a key selected.</param>
/// <param name="RestoreMainWindow">Restores DotDeck from the tray/minimized state and activates it.</param>
/// <param name="ProbeScreen">Renders a hidden tab's recent output and reads the screen (estimate; null on failure).</param>
public sealed record ApprovalShellBridge(
    Func<IEnumerable<TerminalPaneViewModel>> Terminals,
    Action<TerminalPaneViewModel> ShowTerminal,
    Action<AiSession> ShowAiTerminal,
    Action<string> RevealInMonitor,
    Action RestoreMainWindow,
    Func<TerminalPaneViewModel, CancellationToken, Task<string?>> ProbeScreen);

/// <summary>
/// Connects the approval sources to <see cref="ApprovalService"/> and the service to the views:
/// <list type="bullet">
/// <item>PermissionRequest hooks of interactive Claude sessions (one <see cref="NamedPipeApprovalHost"/> per session);</item>
/// <item>the fallback detection of terminal tabs (prompt pattern on the screen + output stopped for
/// <see cref="ScreenApprovalProvider.QuietTime"/>), for Codex sessions and CLIs run by hand;</item>
/// <item>the hand-off rule of hook requests: when DotDeck is active and the session's terminal tab is shown (at
/// request time or later), the hook stops waiting and the terminal prompt decides;</item>
/// <item>the per-session view models (AI 모니터, cards) refreshed every second and on every change.</item>
/// </list>
/// Runs on the UI thread; hook and refresh-timer callbacks are posted to it.
/// </summary>
public sealed class ApprovalCoordinator : IApprovalActions, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ApprovalService _service;
    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<CliKind, ScreenApprovalProvider> _screenProviders;
    private readonly Func<int, (string Name, DateTimeOffset? StartedAt)?> _inspect;
    private readonly Func<int, string?> _imagePath;
    private readonly Func<string, Task<string?>> _versionOf;
    private readonly Dictionary<string, ApprovalItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AiSession Session, NamedPipeApprovalHost Host)> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<TerminalPaneViewModel, PaneWatch> _watches = [];
    private readonly Dictionary<string, string?> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _carded = new(StringComparer.Ordinal);

    // Hook requests: the session terminal's input count when the request arrived (pipe threads add, UI thread reads).
    // An entry is recorded before the request is added to the service; the timer removes it only once the service no
    // longer has the request and the entry is older than BaselineGrace, so it is never dropped before the Add.
    private static readonly TimeSpan BaselineGrace = TimeSpan.FromSeconds(10);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TerminalConnection Terminal, long Count, DateTimeOffset At)> _inputBaseline =
        new(StringComparer.Ordinal);
    private ApprovalShellBridge? _bridge;
    private ITimer? _timer;
    private bool _isAppActive = true;
    private bool _disposed;

    public ApprovalCoordinator(
        ApprovalService service,
        ScreenPromptCatalog catalog,
        Func<int, (string Name, DateTimeOffset? StartedAt)?> inspect,
        Func<int, string?> imagePath,
        Func<string, Task<string?>> versionOf,
        SynchronizationContext? ui = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(catalog);
        _inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
        _imagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        _versionOf = versionOf ?? throw new ArgumentNullException(nameof(versionOf));
        _ui = ui ?? SynchronizationContext.Current;
        _time = service.Time;
        _screenProviders = new()
        {
            [CliKind.Claude] = new ScreenApprovalProvider(CliKind.Claude, catalog),
            [CliKind.Codex] = new ScreenApprovalProvider(CliKind.Codex, catalog),
            [CliKind.Antigravity] = new ScreenApprovalProvider(CliKind.Antigravity, catalog),
        };
        _service.Changed += (_, _) => Post(Refresh);
    }

    public ApprovalService Service => _service;

    /// <summary>After every refresh (AI 모니터 order and badge, notification cards).</summary>
    public event EventHandler? Updated;

    /// <summary>A request got a notification card for the first time (sound).</summary>
    public event EventHandler? CardAdded;

    /// <summary>
    /// A DotDeck window is the foreground window. Becoming active closes the notification cards: what was waiting
    /// is now shown in AI 모니터 and does not pop up again.
    /// </summary>
    public bool IsAppActive
    {
        get => _isAppActive;
        set
        {
            if (_isAppActive == value)
            {
                return;
            }

            _isAppActive = value;
            if (value)
            {
                MarkWaitingSeen();
                ApplyHandOffRules();
            }

            Refresh();
        }
    }

    /// <summary>Waiting requests, oldest first, as their sessions' view models.</summary>
    public IReadOnlyList<ApprovalItemViewModel> WaitingItems =>
        [.. _service.Waiting.Select(r => r.SessionKey).Distinct(StringComparer.Ordinal)
            .Select(k => _items.GetValueOrDefault(k)).OfType<ApprovalItemViewModel>().Where(i => i.IsWaiting)];

    /// <summary>Items that get a notification card now (waiting, not yet seen in DotDeck, not closed with ×).</summary>
    public IReadOnlyList<ApprovalItemViewModel> CardItems =>
        [.. WaitingItems.Where(i => i.Request is { } r && !_seen.Contains(r.Id) && !_dismissed.Contains(r.Id))];

    /// <summary>The key of a CLI run by hand in a plain terminal tab (AI 모니터 row).</summary>
    public static string TerminalKey(TerminalPaneViewModel pane, CliKind cli)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return "term:" + pane.ContentId + ":" + cli;
    }

    /// <summary>The view model of a session's approvals (created on first use).</summary>
    public ApprovalItemViewModel ItemFor(string sessionKey, string title)
    {
        if (!_items.TryGetValue(sessionKey, out var item))
        {
            item = new ApprovalItemViewModel(sessionKey, title, this);
            _items.Add(sessionKey, item);
            item.Update(_service.CurrentFor(sessionKey), _service.LastFor(sessionKey), Math.Max(0, _service.WaitingCount(sessionKey) - 1), _time.GetUtcNow());
        }
        else if (item.SessionTitle != title)
        {
            item.SessionTitle = title;
        }

        return item;
    }

    public void Attach(ApprovalShellBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _timer ??= _time.CreateTimer(_ => Post(Tick), null, RefreshInterval, RefreshInterval);
    }

    /// <summary>Starts the approval channel of an interactive Claude session (called while the session starts).</summary>
    public IDisposable StartHost(AiSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var host = new NamedPipeApprovalHost(session.Directory, session.Id, new HookSink(this, session));
        Post(() => _hosts[session.Id] = (session, host));
        return host;
    }

    /// <summary>Timer: hook bookkeeping, screen detection, hand-off rules, refresh.</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var (id, baseline) in _inputBaseline.ToList())
        {
            if (now - baseline.At > BaselineGrace && _service.Find(id) is null)
            {
                _inputBaseline.TryRemove(id, out _);
            }
        }

        foreach (var (id, (session, host)) in _hosts.ToList())
        {
            if (session.State != AiSessionState.Running)
            {
                _service.SessionEnded(id);
                host.SetPending(false);
                _hosts.Remove(id);
            }
        }

        ScanTerminals();
        ApplyHandOffRules();
        Refresh();
    }

    public Task RespondAsync(string requestId, ApprovalDecision decision)
    {
        // Typing into the session's terminal after the request means the prompt is (being) answered there: a late
        // answer from DotDeck would be recorded as DotDeck's although the terminal decided. Hand it to the terminal.
        if (_inputBaseline.TryGetValue(requestId, out var baseline) && baseline.Terminal.InputCount != baseline.Count)
        {
            _service.HandOff(requestId);
            return Task.CompletedTask;
        }

        return _service.RespondAsync(requestId, decision, ApprovalResponder.User);
    }

    public void GoToTerminal(string sessionKey)
    {
        if (_bridge is not { } bridge)
        {
            return;
        }

        bridge.RestoreMainWindow();
        if (_hosts.TryGetValue(sessionKey, out var hosted))
        {
            bridge.ShowAiTerminal(hosted.Session);
            return;
        }

        foreach (var pane in bridge.Terminals())
        {
            if (pane.Session?.Id == sessionKey)
            {
                bridge.ShowAiTerminal(pane.Session);
                return;
            }

            if (sessionKey.StartsWith("term:" + pane.ContentId + ":", StringComparison.Ordinal))
            {
                bridge.ShowTerminal(pane);
                return;
            }
        }
    }

    public void Open(string sessionKey)
    {
        if (_bridge is not { } bridge)
        {
            return;
        }

        bridge.RestoreMainWindow();
        bridge.RevealInMonitor(sessionKey);
    }

    public void DismissCard(string requestId)
    {
        _dismissed.Add(requestId);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        foreach (var watch in _watches.Values)
        {
            watch.Detach();
        }

        _watches.Clear();
    }

    // Every item is updated from the service; panes get the bar of their session; hooks learn whether to report.
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var (key, item) in _items)
        {
            item.Update(_service.CurrentFor(key), _service.LastFor(key), Math.Max(0, _service.WaitingCount(key) - 1), now);
        }

        foreach (var (id, (_, host)) in _hosts)
        {
            host.SetPending(_service.CurrentFor(id) is not null);
        }

        if (_isAppActive)
        {
            MarkWaitingSeen();
        }

        foreach (var item in CardItems)
        {
            if (item.Request is { } r && _carded.Add(r.Id))
            {
                CardAdded?.Invoke(this, EventArgs.Empty);
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void MarkWaitingSeen()
    {
        foreach (var request in _service.Waiting)
        {
            _seen.Add(request.Id);
        }
    }

    /// <summary>
    /// While DotDeck is active and a session's terminal tab is shown (at request time, or once the user moves there,
    /// directly or with [터미널로 이동]), DotDeck stops waiting for its hook requests so the terminal prompt alone decides.
    /// </summary>
    private void ApplyHandOffRules()
    {
        if (!_isAppActive || _bridge is not { } bridge)
        {
            return;
        }

        var shownSessions = bridge.Terminals()
            .Where(p => p.Session is not null && p.IsVisible && p.IsSelected)
            .Select(p => p.Session!.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var request in _service.Waiting)
        {
            if (request is { Source: ApprovalSource.Hook, State: ApprovalState.Pending } && shownSessions.Contains(request.SessionKey))
            {
                _service.HandOff(request.Id);
            }
        }
    }

    // ---- Fallback detection (screen pattern + stopped output) ----

    private void ScanTerminals()
    {
        if (_bridge is not { } bridge)
        {
            return;
        }

        var settings = _service.Settings;
        var panes = bridge.Terminals().Where(p => p.IsRunning).ToList();
        foreach (var (pane, watch) in _watches.ToList())
        {
            if (!panes.Contains(pane))
            {
                watch.Detach();
                _watches.Remove(pane);
                foreach (var key in watch.Keys)
                {
                    _service.SessionEnded(key);
                }
            }
        }

        if (!settings.Enabled || !settings.ScreenDetection)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var pane in panes)
        {
            var clis = ClisIn(pane, settings);
            if (clis.Count == 0)
            {
                continue;
            }

            if (!_watches.TryGetValue(pane, out var watch))
            {
                watch = new PaneWatch(pane, _time);
                _watches.Add(pane, watch);
            }

            foreach (var (cli, executable) in clis)
            {
                var key = pane.Session?.Id ?? TerminalKey(pane, cli);
                watch.Keys.Add(key);
                if (executable is not null)
                {
                    EnsureVersion(executable);
                }

                if (watch.LastOutput is not { } last || now - last < ScreenApprovalProvider.QuietTime
                    || watch.Checking || watch.CheckedOutput == last)
                {
                    continue;
                }

                _ = CheckScreenAsync(pane, watch, cli, key, executable, last);
            }
        }
    }

    // The CLIs of a tab answered by screen detection: a Codex session, or claude/codex/agy processes in a plain terminal.
    // Interactive Claude sessions use their hook instead.
    private List<(CliKind Cli, string? Executable)> ClisIn(TerminalPaneViewModel pane, ApprovalSettings settings)
    {
        var found = new List<(CliKind, string?)>();
        if (pane.Session is { } session && (session.HasApprovalHooks || session.State != AiSessionState.Running))
        {
            return found;
        }

        foreach (var id in pane.Connection.JobProcessIds())
        {
            if (_inspect(id) is not { } info || AiMonitorPaneViewModel.ClassifyProcess(info.Name) is not { } cli
                || (pane.Session is { } s && s.Cli != cli) || found.Any(f => f.Item1 == cli) || !settings.Detects(cli))
            {
                continue;
            }

            found.Add((cli, _imagePath(id)));
        }

        return found;
    }

    private void EnsureVersion(string executable)
    {
        if (_versions.ContainsKey(executable))
        {
            return;
        }

        _versions[executable] = null;
        _ = Task.Run(async () =>
        {
            string? version;
            try
            {
                version = ScreenApprovalProvider.ParseVersion(await _versionOf(executable).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                version = null;
            }

            Post(() => _versions[executable] = version);
        });
    }

    private async Task CheckScreenAsync(TerminalPaneViewModel pane, PaneWatch watch, CliKind cli, string key, string? executable, DateTimeOffset output)
    {
        watch.Checking = true;
        try
        {
            var live = pane.ScreenReader is not null;
            var screen = pane.ScreenReader is { } reader
                ? await reader(CancellationToken.None).ConfigureAwait(true)
                : await _bridge!.ProbeScreen(pane, CancellationToken.None).ConfigureAwait(true);
            watch.CheckedOutput = output;
            if (screen is null)
            {
                return; // tried again after the next output
            }

            if (watch.LastOutput != output)
            {
                return; // new output while reading: the next quiet period decides
            }

            var provider = _screenProviders[cli];
            var match = provider.Match(screen);
            var open = _service.CurrentFor(key) is { Source: ApprovalSource.Screen, State: ApprovalState.Pending } current ? current : null;
            if (match is null)
            {
                if (open is not null)
                {
                    _service.MarkAnsweredInTerminal(open.Id, "터미널에서 응답됨");
                }

                return;
            }

            if (open?.MatchKey == match.Fingerprint)
            {
                return;
            }

            if (open is not null)
            {
                _service.MarkAnsweredInTerminal(open.Id, "터미널에서 응답됨");
            }

            var version = executable is not null ? _versions.GetValueOrDefault(executable) : null;
            var folder = pane.Connection.WorkingDirectory ?? pane.Session?.Scope.Primary.Root;
            var request = provider.ToRequest(match, key, folder, version, _time.GetUtcNow(), live);
            _ = ItemFor(key, pane.Session is { } session ? session.Title : CliName(cli) + " · " + pane.DisplayName);
            _service.Add(request, new ScreenChannel(this, pane, watch, provider, request, version));
        }
        finally
        {
            watch.Checking = false;
        }
    }

    private static string CliName(CliKind cli) => cli switch
    {
        CliKind.Claude => "Claude",
        CliKind.Codex => "Codex",
        _ => "agy",
    };

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

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try
            {
                completion.TrySetResult(await action().ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                completion.TrySetException(ex);
            }
        }, null);
        return completion.Task;
    }

    /// <summary>Last output time of a terminal (C# side: when the VT stream arrived).</summary>
    private sealed class PaneWatch
    {
        private readonly TerminalPaneViewModel _pane;
        private readonly TimeProvider _time;
        private long _lastOutputTicks;

        public PaneWatch(TerminalPaneViewModel pane, TimeProvider time)
        {
            _pane = pane;
            _time = time;
            // The recent output already on screen counts as output "now": a prompt that is already shown is found
            // after one quiet period.
            if (pane.Connection.Attach(OnOutput).Length > 0)
            {
                OnOutput(string.Empty);
            }
        }

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public bool Checking { get; set; }

        public DateTimeOffset? CheckedOutput { get; set; }

        public DateTimeOffset? LastOutput
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastOutputTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public void Detach() => _pane.Connection.Detach(OnOutput);

        private void OnOutput(string text) => Interlocked.Exchange(ref _lastOutputTicks, _time.GetUtcNow().UtcTicks);
    }

    /// <summary>
    /// Key presses for a screen request. Right before typing, the tab's live screen is read again: the same prompt must
    /// still be there and the output must still be quiet; otherwise nothing is typed.
    /// </summary>
    private sealed class ScreenChannel(
        ApprovalCoordinator owner, TerminalPaneViewModel pane, PaneWatch watch, ScreenApprovalProvider provider, ApprovalRequest request, string? version) : IApprovalChannel
    {
        public Task<bool> SendAsync(ApprovalDecision decision, CancellationToken cancellationToken) => owner.OnUiAsync(async () =>
        {
            if (!pane.IsRunning || pane.ScreenReader is not { } reader || watch.LastOutput is not { } before
                || owner._time.GetUtcNow() - before < ScreenApprovalProvider.QuietTime)
            {
                return false; // output is not (or no longer) quiet: the screen may be changing
            }

            var screen = await reader(cancellationToken).ConfigureAwait(true);
            var match = provider.Match(screen);
            if (match is null || match.Fingerprint != request.MatchKey || watch.LastOutput != before
                || owner._time.GetUtcNow() - before < ScreenApprovalProvider.QuietTime
                || ScreenApprovalProvider.KeysFor(match, decision, version) is not { } keys)
            {
                return false;
            }

            pane.Connection.Send(keys);
            return true;
        });

        public void Release()
        {
        }
    }

    /// <summary>Hook reports of one session (pipe threads).</summary>
    private sealed class HookSink(ApprovalCoordinator owner, AiSession session) : IApprovalHookSink
    {
        public void OnRequest(ClaudeApprovalHook.RequestMessage message, ApprovalHookConnection connection)
        {
            if (owner._disposed || !owner._service.Settings.Detects(CliKind.Claude) || session.State != AiSessionState.Running)
            {
                connection.Release(); // detection off: the CLI prompt decides right away
                return;
            }

            var request = ClaudeApprovalHook.ToRequest(message, session.Id, owner._time.GetUtcNow());
            if (session.Terminal is { } terminal)
            {
                owner._inputBaseline[request.Id] = (terminal, terminal.InputCount, owner._time.GetUtcNow());
            }

            connection.Disconnected += (_, _) => owner._service.HookEnded(request.Id);
            owner.Post(() => owner.ItemFor(session.Id, session.Title));
            owner._service.Add(request, connection);
            if (!connection.IsOpen)
            {
                owner._service.HookEnded(request.Id); // the hook ended before it was registered
            }

            owner.Post(owner.ApplyHandOffRules);
        }

        public void OnToolRan(string match) => owner._service.MarkToolRan(session.Id, match);
    }
}
