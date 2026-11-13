using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>What an AI session's terminal tab can ask the shell to do (toolbar and scope chips).</summary>
public sealed record AiTerminalActions(
    Func<AiSession, Task> ReviewChanges,
    Action<AiSession> Stop,
    Func<AiSession, Task> RestartWithProjects,
    Func<AiSession, string, string?> ExcludeProject,
    Action<string> CopyText);

/// <summary>One project of a session scope shown as a chip on the terminal toolbar (SPEC 11.5 A).</summary>
public sealed partial class ScopeChipViewModel(TerminalPaneViewModel owner, string projectId, string name, string root, bool isPrimary) : ObservableObject
{
    public string ProjectId { get; } = projectId;

    public string Name { get; } = name;

    public string Root { get; } = root;

    public bool IsPrimary { get; } = isPrimary;

    public string ToolTip => IsPrimary ? $"주 프로젝트\n{Root}" : $"추가 프로젝트\n{Root}";

    public bool CanExclude => !IsPrimary && owner.IsRunning;

    [RelayCommand]
    private void CopyPath() => owner.CopyRoot(Root);

    [RelayCommand]
    private void Exclude() => owner.ExcludeFromScope(this);

    internal void Refresh() => OnPropertyChanged(nameof(CanExclude));
}

/// <summary>
/// One terminal tab in the bottom area (ContentId <c>tool.terminal:&lt;id&gt;</c>, restored as an idle shell).
/// The view (TerminalView) connects xterm.js to <see cref="Connection"/>. Closing the tab asks the shell,
/// which stops a running process tree after confirmation and removes the tab. A tab of an interactive AI session
/// (<see cref="Session"/>) also shows the scope chips, the guard state and review / stop / restart actions.
/// </summary>
public sealed partial class TerminalPaneViewModel : ToolPaneViewModel
{
    public const string ContentIdPrefix = "tool.terminal:";

    private string _baseTitle;
    private readonly Action<string> _copy;
    private readonly AiTerminalActions? _actions;

    public TerminalPaneViewModel(string id, string title, TerminalConnection connection, Action<string> copy, SynchronizationContext? ui)
        : this(id, title, connection, copy, ui, session: null, actions: null)
    {
    }

    public TerminalPaneViewModel(AiSession session, AiTerminalActions actions, SynchronizationContext? ui)
        : this(session?.Id ?? throw new ArgumentNullException(nameof(session)), session.Title,
            session.Terminal ?? throw new ArgumentException("not an interactive session", nameof(session)),
            (actions ?? throw new ArgumentNullException(nameof(actions))).CopyText, ui, session, actions)
    {
    }

    private TerminalPaneViewModel(string id, string title, TerminalConnection connection, Action<string> copy, SynchronizationContext? ui, AiSession? session, AiTerminalActions? actions)
        : base(ContentIdPrefix + id, title, ToolLocation.Bottom)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(copy);
        _baseTitle = title;
        _copy = copy;
        _actions = actions;
        Session = session;
        Connection = connection;
        RefreshSession();
        _ = WatchExitAsync(ui);
    }

    public TerminalConnection Connection { get; }

    public string PersistentTitle => _baseTitle;

    /// <summary>The interactive AI session shown in this tab, or null for a plain terminal.</summary>
    public AiSession? Session { get; }

    public bool IsAiSession => Session is not null;

    public ObservableCollection<ScopeChipViewModel> Chips { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(RestartWithProjectsCommand))]
    private bool _isRunning = true;

    /// <summary>Guard and session state for the toolbar ("가드 확인 대기", "가드 적용됨", "중지됨: …").</summary>
    [ObservableProperty]
    private string _sessionState = string.Empty;

    public int? ExitCode { get; private set; }

    /// <summary>The user closed (hid) the tab; the shell decides whether it really goes away.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The process ended; raised on the UI thread.</summary>
    public event EventHandler? Ended;

    /// <summary>Line written into the terminal once the process has ended.</summary>
    public string EndNotice => ExitCode is { } code
        ? string.Create(CultureInfo.InvariantCulture, $"\r\n\u001b[90m[프로세스가 종료되었습니다. 코드 {code}]\u001b[0m\r\n")
        : "\r\n\u001b[90m[프로세스가 종료되었습니다]\u001b[0m\r\n";

    /// <summary>Text selected in the terminal and copied by the user (Ctrl+C with a selection).</summary>
    public void Copy(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _copy(text);
        }
    }

    internal void CopyRoot(string root) => Copy(root);

    internal void ExcludeFromScope(ScopeChipViewModel chip)
    {
        if (Session is { } session && _actions is { } actions && chip.CanExclude)
        {
            actions.ExcludeProject(session, chip.ProjectId);
            RefreshSession();
        }
    }

    [RelayCommand]
    private Task ReviewChangesAsync() => Session is { } session && _actions is { } actions ? actions.ReviewChanges(session) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (Session is { } session && _actions is { } actions)
        {
            actions.Stop(session);
        }
    }

    private bool CanStop() => IsAiSession && IsRunning;

    /// <summary>"프로젝트 추가/세션 재시작": the start dialog with this scope, continuing the same conversation.</summary>
    [RelayCommand(CanExecute = nameof(IsAiSession))]
    private Task RestartWithProjectsAsync() =>
        Session is { } session && _actions is { } actions ? actions.RestartWithProjects(session) : Task.CompletedTask;

    /// <summary>Re-reads the session's scope and state (call on the UI thread after the manager changed).</summary>
    public void RefreshSession()
    {
        if (Session is not { } session)
        {
            return;
        }

        // The title follows the scope (a project excluded from the session leaves the title too).
        _baseTitle = session.Title;
        Title = IsRunning ? _baseTitle : _baseTitle + " (종료됨)";

        var scope = session.Scope;
        var wanted = scope.Projects.Select(p => (p.ProjectId, p.Name, p.Root, IsPrimary: p.ProjectId == scope.Primary.ProjectId)).ToList();
        if (!wanted.SequenceEqual(Chips.Select(c => (c.ProjectId, c.Name, c.Root, c.IsPrimary))))
        {
            Chips.Clear();
            foreach (var p in wanted)
            {
                Chips.Add(new ScopeChipViewModel(this, p.ProjectId, p.Name, p.Root, p.IsPrimary));
            }
        }

        foreach (var chip in Chips)
        {
            chip.Refresh();
        }

        SessionState = session.State switch
        {
            AiSessionState.Running when session.Cli == DotDeck.Core.AiCli.Launch.CliKind.Codex => "Codex sandbox · 읽기 제한 없음",
            AiSessionState.Running => session.GuardConnected ? "가드 적용됨" : "가드 확인 대기",
            AiSessionState.Exited => "종료됨",
            AiSessionState.Stopped => "중지됨" + (session.Message is { } m ? ": " + m : string.Empty),
            _ => "실패" + (session.Message is { } f ? ": " + f : string.Empty),
        };
    }

    protected override void OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        foreach (var chip in Chips)
        {
            chip.Refresh();
        }
    }

    private async Task WatchExitAsync(SynchronizationContext? ui)
    {
        int code;
        try
        {
            code = await Connection.Exited.ConfigureAwait(false);
            await Connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            code = Connection.Exited.Result;
        }

        void Apply()
        {
            ExitCode = code;
            IsRunning = false;
            Title = _baseTitle + " (종료됨)";
            RefreshSession();
            Ended?.Invoke(this, EventArgs.Empty);
        }

        if (ui is null)
        {
            Apply();
        }
        else
        {
            ui.Post(_ => Apply(), null);
        }
    }
}
