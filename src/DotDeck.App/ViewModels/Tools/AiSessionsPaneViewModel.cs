using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// "AI 작업" panel (SPEC 11.3-4): the sessions of this run, each with its parsed output, state, guard status, stop and
/// change review. Session events arrive on background threads and are marshalled to the UI thread.
/// </summary>
public sealed partial class AiSessionsPaneViewModel : ToolPaneViewModel
{
    private readonly SynchronizationContext? _ui;
    private readonly Func<AiSession, Task> _reviewChanges;
    private readonly Func<AiSession, bool> _confirmStop;
    private AiSessionManager? _manager;
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);

    public AiSessionsPaneViewModel(Func<AiSession, Task> reviewChanges, Func<AiSession, bool> confirmStop)
        : base("tool.ai", "AI 작업", ToolLocation.Bottom)
    {
        _ui = SynchronizationContext.Current;
        _reviewChanges = reviewChanges ?? throw new ArgumentNullException(nameof(reviewChanges));
        _confirmStop = confirmStop ?? throw new ArgumentNullException(nameof(confirmStop));
    }

    public ObservableCollection<AiSessionItemViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private AiSessionItemViewModel? _selectedSession;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>Follows the manager: new sessions appear (and are selected) as they start.</summary>
    public void Attach(AiSessionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        manager.Changed += (_, _) => Post(Sync);
        Sync();
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
                var item = new AiSessionItemViewModel(session, Post, Stop, _reviewChanges);
                Sessions.Insert(0, item);
                SelectedSession = item;
                IsVisible = true;
            }
        }

        OnPropertyChanged(nameof(HasSessions));
    }

    private void Stop(AiSession session)
    {
        if (_manager is not null && session.State == AiSessionState.Running && _confirmStop(session))
        {
            _manager.Stop(session);
        }
    }

    /// <summary>Removes finished sessions from the list (their folders stay until cleanup).</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Sessions.Where(s => !s.IsRunning).ToList())
        {
            item.Detach();
            _cleared.Add(item.Session.Id);
            Sessions.Remove(item);
        }

        SelectedSession ??= Sessions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSessions));
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

/// <summary>One session row and its output.</summary>
public sealed partial class AiSessionItemViewModel : ObservableObject
{
    public const int MaxLines = 5000;

    private readonly Action<Action> _post;
    private readonly Action<AiSession> _stop;
    private readonly Func<AiSession, Task> _review;

    internal AiSessionItemViewModel(AiSession session, Action<Action> post, Action<AiSession> stop, Func<AiSession, Task> review)
    {
        Session = session;
        _post = post;
        _stop = stop;
        _review = review;

        // Lines raised before this row existed come with the subscription, so none is lost or shown twice.
        foreach (var (line, isError) in session.AttachOutput(OnOutput))
        {
            AddLine(line, isError);
        }

        session.StateChanged += OnStateChanged;
    }

    public AiSession Session { get; }

    public ObservableCollection<AiOutputEvent> Lines { get; } = [];

    public string Title => Session.Title;

    public string StartedText => Session.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CommandLine => Session.CommandLinePreview;

    /// <summary>The prompt as shown (secret-looking assignments masked like all AI output).</summary>
    public string? Prompt => Session.Request.Prompt is { } prompt ? DotDeck.Core.Security.SecretMasker.MaskText(prompt) : null;

    public bool IsRunning => Session.State == AiSessionState.Running;

    public string StateText => Session.State switch
    {
        AiSessionState.Running when !Session.GuardConnected => "실행 중 · 가드 확인 대기",
        AiSessionState.Running => "실행 중 · 가드 적용됨",
        AiSessionState.Exited => "완료",
        AiSessionState.Stopped => "중지됨",
        _ => Session.ExitCode is { } code ? $"실패 (종료 코드 {code})" : "실패",
    };

    public string? Message => Session.Message;

    public bool IsFailed => Session.State == AiSessionState.Failed;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _stop(Session);

    [RelayCommand]
    private Task ReviewChanges() => _review(Session);

    internal void Detach()
    {
        Session.DetachOutput(OnOutput);
        Session.StateChanged -= OnStateChanged;
    }

    private void OnOutput(AiSession session, string line, bool isError) => _post(() => AddLine(line, isError));

    private void OnStateChanged(AiSession session) => _post(() =>
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Message));
        StopCommand.NotifyCanExecuteChanged();
    });

    private void AddLine(string line, bool isError)
    {
        var events = Session.Cli == CliKind.Claude
            ? ClaudeStreamParser.Parse(line, isError)
            : [new AiOutputEvent(AiOutputKind.Raw, DotDeck.Core.Security.SecretMasker.MaskText(line), isError)];
        foreach (var e in events)
        {
            Lines.Add(e);
        }

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }
}
