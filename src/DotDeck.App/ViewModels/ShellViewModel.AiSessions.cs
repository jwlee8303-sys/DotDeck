using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Presets;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Logging;

namespace DotDeck.App.ViewModels;

/// <summary>AI CLI sessions (SPEC 11): status bar count, scope sync with the project list, exit confirmation.</summary>
public sealed partial class ShellViewModel
{
    private AiSessionManager? _aiSessions;
    private SynchronizationContext? _uiContext;
    private DotDeck.Core.Projects.ProjectRegistry? _aiRegistry;

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
        _aiRegistry = registry;
        _uiContext = SynchronizationContext.Current;
        manager.Changed += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(AiStatusText));
            OnPropertyChanged(nameof(HasRunningAiSessions));
            OfferReviewForFinishedSessions();
        });
        AiWork.Attach(manager);

        // Scope narrowing must not wait for anything: the guard reads scope.json on the next tool call.
        registry.Changed += (_, _) => manager.OnProjectsChanged();
    }

    /// <summary>Tools menu "Claude 세션 시작…": the dialog preselects the project selected in the project area.</summary>
    [RelayCommand]
    private Task StartAiSessionAsync() => LaunchAiSessionAsync(DefaultAiProjectId());

    /// <summary>
    /// The project of the active document, else the project selected in the project area (SPEC 11.3-3). Built-in
    /// (global) projects are never the default; they can still be picked in the dialog.
    /// </summary>
    private string? DefaultAiProjectId()
    {
        var explorers = Projects.Explorers.Where(e => !e.Project.IsBuiltin).ToList();
        if (ActiveTextDocument?.Buffer.Path is { } path)
        {
            if (SessionLaunchViewModel.OwnerOf(path, explorers.Select(e => (e.Project.Id, e.Source.RootPath))) is { } owner)
            {
                return owner;
            }
        }

        return Projects.List.SelectedRow?.Explorer?.Project is { IsBuiltin: false } selected ? selected.Id : null;
    }

    /// <summary>
    /// Saves open documents, shows the start dialog and starts the session (SPEC 11.3-3). In 5a the quick start from
    /// a project also opens the dialog, because a command-mode session needs a prompt.
    /// </summary>
    internal async Task LaunchAiSessionAsync(string? primaryProjectId)
    {
        if (_aiSessions is not { } manager || _aiRegistry is not { } registry)
        {
            _dialogs.ShowError("Claude 세션 시작", "AI 세션 기능을 사용할 수 없습니다.");
            return;
        }

        // The start snapshot and the AI must see what the editor shows.
        await SaveAllAsync().ConfigureAwait(true);
        if (HasUnsavedDocuments
            && !_dialogs.Confirm("Claude 세션 시작", "저장하지 않은 변경이 있습니다.\n\nAI는 디스크에 저장된 내용을 기준으로 작업합니다. 계속할까요?"))
        {
            return;
        }

        var presets = manager.Presets;
        var dialog = new SessionLaunchViewModel(
            registry.Projects, Paths, _store, primaryProjectId,
            id => presets?.GetLastAdditional(id) ?? [],
            manager.PreviewCommandLine,
            canSavePresets: presets is { Status: not PresetStoreLoadStatus.ReadOnly });
        if (_dialogs.ShowSessionLaunch(dialog) is not { } request)
        {
            return;
        }

        var openDocuments = _buffers.Buffers.Select(b => b.Path).ToList();
        request = request with
        {
            OpenDocuments = request.IncludeOpenDocuments ? openDocuments : [],
            ActiveDocument = request.IncludeOpenDocuments ? ActiveTextDocument?.Buffer.Path : null,
        };

        AiSession session;
        try
        {
            session = await Task.Run(() => manager.Start(request)).ConfigureAwait(true);
        }
        catch (AiSessionStartException ex)
        {
            _dialogs.ShowError("Claude 세션 시작", ex.Message);
            return;
        }

        RememberLaunchChoices(presets, request, dialog);
        StatusMessage = $"Claude 세션을 시작했습니다: {session.Title}";
        _log.Info($"AI 세션 시작: {session.Title} ({session.Id})");
    }

    private void RememberLaunchChoices(SessionPresetStore? presets, AiSessionStartRequest request, SessionLaunchViewModel dialog)
    {
        if (presets is not { Status: not PresetStoreLoadStatus.ReadOnly })
        {
            return;
        }

        try
        {
            presets.SetLastAdditional(request.PrimaryProjectId, request.AdditionalProjectIds);
            if (dialog.SaveAsPreset)
            {
                presets.SavePreset(new SessionPreset
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = dialog.PresetName.Trim(),
                    PrimaryProjectId = request.PrimaryProjectId,
                    AdditionalProjectIds = request.AdditionalProjectIds,
                    Cli = request.Cli,
                    Mode = request.Mode,
                    IncludeOpenDocuments = request.IncludeOpenDocuments,
                    ResumeConversation = request.ResumeConversation,
                    AllowReadOnlyShell = request.AllowReadOnlyShell,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The session runs regardless; only the remembered choices are lost.
            _log.Warning($"세션 선택을 저장하지 못했습니다: {ex.Message}");
        }
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
