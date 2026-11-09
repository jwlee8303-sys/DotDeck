using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.ViewModels;

/// <summary>AI CLI sessions (SPEC 11): status bar count, scope sync with the project list, exit confirmation.</summary>
public sealed partial class ShellViewModel
{
    private AiSessionManager? _aiSessions;
    private SynchronizationContext? _uiContext;

    public AiSessionManager? AiSessions => _aiSessions;

    /// <summary>"AI n 실행 중" while sessions run; empty otherwise.</summary>
    public string AiStatusText => _aiSessions?.RunningCount is > 0 and var n ? $"AI {n} 실행 중" : string.Empty;

    public bool HasRunningAiSessions => _aiSessions?.RunningCount > 0;

    /// <summary>Connects the session manager (once, from App startup; tests run without it).</summary>
    public void AttachAiSessions(AiSessionManager manager, DotDeck.Core.Projects.ProjectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(registry);
        if (_aiSessions is not null)
        {
            throw new InvalidOperationException("AI session manager is already attached.");
        }

        _aiSessions = manager;
        _uiContext = SynchronizationContext.Current;
        manager.Changed += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(AiStatusText));
            OnPropertyChanged(nameof(HasRunningAiSessions));
        });

        // Scope narrowing must not wait for anything: the guard reads scope.json on the next tool call.
        registry.Changed += (_, _) => manager.OnProjectsChanged();
    }

    /// <summary>Asks before exiting while sessions run (they are stopped with the app). False cancels closing.</summary>
    private bool ConfirmStopAiSessions()
    {
        var running = _aiSessions?.RunningCount ?? 0;
        return running == 0
            || _dialogs.Confirm("DotDeck", $"AI 세션 {running}개가 실행 중입니다.\n\n종료하면 세션이 함께 중지됩니다. 종료할까요?");
    }

    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }
}
