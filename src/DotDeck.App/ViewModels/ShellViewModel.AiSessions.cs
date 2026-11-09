using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Dialogs;
using DotDeck.Core.AiCli.Launch;
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
            RefreshAiTerminals();
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
    /// "이 프로젝트에서 Claude 시작" (SPEC 11.3-1): an interactive Claude session on that project alone, without the
    /// dialog. Open documents are saved first, as for every start.
    /// </summary>
    internal Task QuickStartAiSessionAsync(string projectId) => StartAiSessionCoreAsync(new AiSessionStartRequest
    {
        PrimaryProjectId = projectId,
        Mode = LaunchMode.Interactive,
    }, dialog: null, replacing: null);

    internal Task QuickStartCodexSessionAsync(string projectId) => StartAiSessionCoreAsync(new AiSessionStartRequest
    {
        PrimaryProjectId = projectId,
        Cli = CliKind.Codex,
        Mode = LaunchMode.Interactive,
    }, dialog: null, replacing: null);

    /// <summary>Saves open documents, shows the start dialog and starts the session (SPEC 11.3-3).</summary>
    internal Task LaunchAiSessionAsync(string? primaryProjectId) => ShowLaunchDialogAsync(primaryProjectId, restartOf: null);

    /// <summary>
    /// Scope chip "프로젝트 추가/세션 재시작": the dialog with this session's projects, interactive, continuing the same
    /// conversation. The old session is stopped just before the new one starts (one CLI per conversation).
    /// </summary>
    private Task RestartAiSessionAsync(AiSession session) => ShowLaunchDialogAsync(session.Scope.Primary.ProjectId, session);

    private async Task ShowLaunchDialogAsync(string? primaryProjectId, AiSession? restartOf)
    {
        if (_aiSessions is not { } manager || _aiRegistry is not { } registry)
        {
            _dialogs.ShowError("AI 세션 시작", "AI 세션 기능을 사용할 수 없습니다.");
            return;
        }

        var sandboxConfigured = await Task.Run(() => manager.IsCodexSandboxConfigured).ConfigureAwait(true);
        var presets = manager.Presets;
        IReadOnlyList<string> additional = restartOf is null ? [] : [.. restartOf.Scope.Additional.Select(p => p.ProjectId)];
        var dialog = new SessionLaunchViewModel(
            registry.Projects, Paths, _store, primaryProjectId,
            id => restartOf is not null && id == primaryProjectId ? additional : presets?.GetLastAdditional(id) ?? [],
            manager.PreviewCommandLine,
            canSavePresets: presets is { Status: not PresetStoreLoadStatus.ReadOnly },
            codexSandboxConfigured: sandboxConfigured);
        if (restartOf is not null)
        {
            dialog.IsCodex = restartOf.Cli == CliKind.Codex;
            dialog.IsInteractive = restartOf.Mode == LaunchMode.Interactive;
            dialog.ResumeConversation = restartOf.Cli == CliKind.Claude || restartOf.Mode == LaunchMode.NonInteractive;
        }

        if (_dialogs.ShowSessionLaunch(dialog) is not { } request)
        {
            return;
        }

        if (restartOf is not null && request.ResumeConversation && request.Cli == restartOf.Cli)
        {
            request = request with { ResumeConversationId = restartOf.ConversationId };
        }

        await StartAiSessionCoreAsync(request, dialog, restartOf).ConfigureAwait(true);
    }

    /// <summary>
    /// Saves, confirms and starts. <paramref name="replacing"/> (a restart) is stopped only after the user confirmed,
    /// right before the new session starts: the same conversation must never run in two CLIs.
    /// </summary>
    private async Task StartAiSessionCoreAsync(AiSessionStartRequest request, SessionLaunchViewModel? dialog, AiSession? replacing)
    {
        if (_aiSessions is not { } manager)
        {
            _dialogs.ShowError("AI 세션 시작", "AI 세션 기능을 사용할 수 없습니다.");
            return;
        }

        if (request.Cli == CliKind.Codex && !await Task.Run(() => manager.IsCodexSandboxConfigured).ConfigureAwait(true))
        {
            const string warning = "Windows sandbox 설정을 확인할 수 없습니다. Codex에서 먼저 sandbox를 설정하세요.\n비대화형 실행은 사용할 수 없으며, 대화형에서는 권한 요청을 직접 확인해야 합니다. Codex에는 Claude 파일 가드와 읽기 제한이 적용되지 않습니다.";
            if (request.Mode == LaunchMode.NonInteractive) { _dialogs.ShowError("Codex sandbox", warning); return; }
            if (!_dialogs.Confirm("Codex sandbox", warning + "\n\n대화형 세션을 시작할까요?")) return;
        }

        // The start snapshot and the AI must see what the editor shows.
        await SaveAllAsync().ConfigureAwait(true);
        if (HasUnsavedDocuments
            && !_dialogs.Confirm("AI 세션 시작", "저장하지 않은 변경이 있습니다.\n\nAI는 디스크에 저장된 내용을 기준으로 작업합니다. 계속할까요?"))
        {
            return;
        }

        var openDocuments = _buffers.Buffers.Select(b => b.Path).ToList();
        request = request with
        {
            OpenDocuments = request.IncludeOpenDocuments ? openDocuments : [],
            ActiveDocument = request.IncludeOpenDocuments ? ActiveTextDocument?.Buffer.Path : null,
        };

        var stoppedOld = false;
        if (replacing is { State: AiSessionState.Running })
        {
            manager.Stop(replacing, "새 범위로 다시 시작했습니다.");
            stoppedOld = true;
        }

        if (replacing is not null)
        {
            // The conversation stays reserved until the old CLI process has really ended.
            await replacing.WhenProcessEnded.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        AiSession session;
        try
        {
            session = await Task.Run(() => manager.Start(request)).ConfigureAwait(true);
        }
        catch (AiSessionStartException ex)
        {
            _dialogs.ShowError("AI 세션 시작", stoppedOld
                ? $"{ex.Message}\n\n기존 세션은 중지되었습니다. 중지된 세션 탭의 \"프로젝트 추가/다시 시작…\"으로 같은 대화를 다시 시작할 수 있습니다."
                : ex.Message);
            return;
        }

        if (dialog is not null)
        {
            RememberLaunchChoices(manager.Presets, request, dialog);
        }

        if (session.Terminal is not null)
        {
            ShowAiTerminal(session);
        }

        StatusMessage = $"Claude 세션을 시작했습니다: {session.Title}";
        _log.Info($"AI 세션 시작: {session.Title} ({session.Id}, {(session.Mode == LaunchMode.Interactive ? "대화형" : "명령 실행")})");
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
