using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;

namespace DotDeck.App.ViewModels;

/// <summary>
/// Terminal tabs: plain PowerShell terminals (SPEC 11.3-4) and interactive AI sessions (SPEC 11.5 A), each in a pseudo
/// console with its own Job. Closing a running tab asks first; for an AI session it stops the session.
/// </summary>
public sealed partial class ShellViewModel
{
    private ITerminalLauncher? _terminalLauncher;
    private Func<ShellLocation?>? _locateShell;
    private AppPaths? _terminalPaths;
    private int _terminalNumber;

    public IEnumerable<TerminalPaneViewModel> Terminals => Tools.OfType<TerminalPaneViewModel>();

    /// <summary>Running plain terminals (AI sessions are counted and confirmed by the session manager).</summary>
    public int RunningTerminalCount => Terminals.Count(t => t.IsRunning && !t.IsAiSession);

    /// <summary>Connects the terminal launcher (once, from App startup; tests pass fakes).</summary>
    public void AttachTerminals(ITerminalLauncher launcher, Func<ShellLocation?> locateShell, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(locateShell);
        ArgumentNullException.ThrowIfNull(paths);
        _terminalLauncher = launcher;
        _locateShell = locateShell;
        _terminalPaths = paths;
        _uiContext ??= SynchronizationContext.Current;
    }

    /// <summary>
    /// Tools menu "새 PowerShell 터미널": opens in the folder of the active document's project, else the project
    /// selected in the project area, else the (sandbox) home folder.
    /// </summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        if (_terminalLauncher is not { } launcher || _locateShell is not { } locate || _terminalPaths is not { } paths)
        {
            _dialogs.ShowError("터미널", "터미널 기능을 사용할 수 없습니다.");
            return;
        }

        if (locate() is not { } shell)
        {
            _dialogs.ShowError("터미널", "PowerShell을 찾을 수 없습니다. pwsh.exe(PowerShell 7) 또는 Windows PowerShell이 필요합니다.");
            return;
        }

        var folder = TerminalFolder(paths);
        ITerminalProcess process;
        try
        {
            process = launcher.Start(ShellTerminal.Plan(shell, folder, paths), TerminalSize.Default);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning($"터미널을 시작하지 못했습니다: {shell.Executable}", ex);
            _dialogs.ShowError("터미널", $"터미널을 시작하지 못했습니다: {ex.Message}");
            return;
        }

        var number = ++_terminalNumber;
        var title = number == 1 ? shell.DisplayName : string.Create(CultureInfo.InvariantCulture, $"{shell.DisplayName} {number}");
        AddTerminal(new TerminalPaneViewModel(Guid.NewGuid().ToString("N"), title, new TerminalConnection(process, TerminalSize.Default), CopyFromTerminal, _uiContext));
        _log.Info($"터미널 시작: {shell.Executable} ({folder})");
    }

    private string TerminalFolder(AppPaths paths)
    {
        if (DefaultAiProjectId() is { } id
            && Projects.Explorers.FirstOrDefault(e => e.Project.Id == id)?.Source.RootPath is { } root
            && Directory.Exists(root))
        {
            return root;
        }

        return Directory.Exists(paths.Home) ? paths.Home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Shows the terminal tab of an interactive session, creating it when it is not open (also after the exit).</summary>
    internal void ShowAiTerminal(AiSession session)
    {
        if (Terminals.FirstOrDefault(t => t.Session == session) is { } open)
        {
            open.IsVisible = true;
            open.IsSelected = true;
            open.IsActive = true;
            return;
        }

        if (session.Terminal is null)
        {
            return;
        }

        AddTerminal(new TerminalPaneViewModel(session, new AiTerminalActions(
            ReviewAiChangesAsync,
            s => _aiSessions?.Stop(s),
            RestartAiSessionAsync,
            ExcludeFromAiScope,
            CopyFromTerminal), _uiContext));
    }

    private string? ExcludeFromAiScope(AiSession session, string projectId)
    {
        var name = session.Scope.Additional.FirstOrDefault(p => p.ProjectId == projectId)?.Name ?? projectId;
        if (_aiSessions is not { } manager
            || !_dialogs.Confirm("범위에서 제외", $"{name}을(를) 이 세션의 범위에서 뺄까요?\n\n다음 도구 호출부터 이 폴더의 읽기·쓰기가 차단됩니다. 다시 넣으려면 세션을 다시 시작해야 합니다."))
        {
            return null;
        }

        var error = manager.ExcludeProject(session, projectId);
        if (error is not null)
        {
            _dialogs.ShowError("범위에서 제외", error);
        }
        else
        {
            _log.Info($"AI 세션 범위에서 제외: {name} ({session.Id})");
        }

        return error;
    }

    /// <summary>Scope or state of sessions changed: update the chips and state of their terminal tabs.</summary>
    private void RefreshAiTerminals()
    {
        foreach (var pane in Terminals.Where(t => t.IsAiSession))
        {
            pane.RefreshSession();
        }
    }

    internal void AddTerminal(TerminalPaneViewModel pane)
    {
        pane.CloseRequested += OnTerminalCloseRequested;
        pane.Ended += (_, _) => OnPropertyChanged(nameof(RunningTerminalCount));
        Tools.Add(pane);
        pane.IsVisible = true;
        pane.IsSelected = true;
        pane.IsActive = true;
        OnPropertyChanged(nameof(RunningTerminalCount));
    }

    private void CopyFromTerminal(string text)
    {
        if (!_clipboard.TrySetText(text))
        {
            StatusMessage = "클립보드에 복사하지 못했습니다.";
        }
    }

    // Hiding happens inside AvalonDock's close handling: decide afterwards, on the next dispatcher turn.
    private void OnTerminalCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not TerminalPaneViewModel pane)
        {
            return;
        }

        Post(() =>
        {
            if (pane.IsVisible || !Tools.Contains(pane))
            {
                return;
            }

            var question = pane.IsAiSession
                ? $"{pane.Title} 세션이 실행 중입니다.\n\n탭을 닫으면 세션이 중지됩니다. 변경 검토는 AI 작업 패널에서 할 수 있습니다. 닫을까요?"
                : $"{pane.Title}이(가) 실행 중입니다.\n\n탭을 닫으면 터미널과 그 안에서 실행한 프로그램이 모두 종료됩니다. 닫을까요?";
            if (pane.IsRunning && !_dialogs.Confirm("터미널", question))
            {
                pane.IsVisible = true;
                return;
            }

            CloseTerminal(pane);
        });
    }

    private void CloseTerminal(TerminalPaneViewModel pane)
    {
        pane.CloseRequested -= OnTerminalCloseRequested;
        Tools.Remove(pane);
        if (pane.Session is { } session)
        {
            // The manager owns the session's console and releases it after the exit (its replay stays available).
            _aiSessions?.Stop(session, "터미널 탭을 닫아 세션을 중지했습니다.");
        }
        else
        {
            pane.Connection.Dispose();
        }

        OnPropertyChanged(nameof(RunningTerminalCount));
    }

    /// <summary>Asks before exiting while terminals run (they end with the app). False cancels closing.</summary>
    private bool ConfirmCloseTerminals()
    {
        var running = RunningTerminalCount;
        return running == 0
            || _dialogs.Confirm("DotDeck", $"터미널 {running}개가 실행 중입니다.\n\n종료하면 터미널과 그 안에서 실행한 프로그램이 모두 종료됩니다. 종료할까요?");
    }

    /// <summary>Ends every terminal (app exit). The jobs would end with the process anyway; this is explicit.</summary>
    public void CloseAllTerminals()
    {
        foreach (var pane in Terminals.ToList())
        {
            CloseTerminal(pane);
        }
    }
}
