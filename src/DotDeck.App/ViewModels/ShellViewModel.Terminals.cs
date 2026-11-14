using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Explorer;
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

    /// <summary>
    /// Connects the terminal launcher (once, from App startup; tests pass fakes). With <paramref name="inspectProcess"/>
    /// the "AI 모니터" also lists Claude/Codex started by hand in a terminal tab.
    /// </summary>
    public void AttachTerminals(
        ITerminalLauncher launcher,
        Func<ShellLocation?> locateShell,
        AppPaths paths,
        Func<int, (string Name, DateTimeOffset? StartedAt)?>? inspectProcess = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(locateShell);
        ArgumentNullException.ThrowIfNull(paths);
        _terminalLauncher = launcher;
        _locateShell = locateShell;
        _terminalPaths = paths;
        _uiContext ??= SynchronizationContext.Current;
        if (inspectProcess is not null)
        {
            AiMonitor.AttachTerminals(() => [.. Terminals], inspectProcess, ShowTerminal);
        }
    }

    /// <summary>Shows and activates a terminal tab (AI 모니터 double click on a terminal run).</summary>
    internal void ShowTerminal(TerminalPaneViewModel pane)
    {
        if (!Terminals.Contains(pane))
        {
            return; // the tab was closed meanwhile
        }

        pane.IsVisible = true;
        pane.IsSelected = true;
        pane.IsActive = true;
    }

    /// <summary>
    /// Tools menu "새 PowerShell 터미널": opens in the folder of the active document's project, else the project
    /// selected in the project area, else the (sandbox) home folder.
    /// </summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        if (TerminalTarget() is not var (launcher, shell, paths))
        {
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
        AddTerminal(new TerminalPaneViewModel(Guid.NewGuid().ToString("N"), WithPrivilege(title, IsAdministrator), new TerminalConnection(process, TerminalSize.Default, folder), CopyFromTerminal, _uiContext));
        _log.Info($"터미널 시작: {shell.Executable} ({folder}){(IsAdministrator ? " [관리자]" : string.Empty)}");
    }

    /// <summary>Prefix of the default title of a terminal that runs elevated.</summary>
    public const string AdministratorTitlePrefix = "관리자: ";

    /// <summary>
    /// Tools menu "관리자 권한 PowerShell 터미널": a pseudo console runs with DotDeck's own rights, so an elevated tab
    /// is only possible when DotDeck runs elevated; otherwise an elevated PowerShell window opens (UAC).
    /// </summary>
    [RelayCommand]
    private void OpenAdministratorTerminal()
    {
        if (IsAdministrator)
        {
            OpenTerminal();
            return;
        }

        if (TerminalTarget() is not var (_, shell, paths))
        {
            return;
        }

        var folder = TerminalFolder(paths);
        try
        {
            if (!_shellLauncher.OpenElevatedTerminal(shell.Executable, folder))
            {
                StatusMessage = "관리자 권한 터미널을 취소했습니다.";
                return;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning($"관리자 권한 터미널을 시작하지 못했습니다: {shell.Executable}", ex);
            _dialogs.ShowError("터미널", $"관리자 권한 터미널을 시작하지 못했습니다: {ex.Message}");
            return;
        }

        StatusMessage = "관리자 권한 PowerShell을 별도 창으로 열었습니다. DotDeck 탭 안에서 쓰려면 DotDeck을 관리자 권한으로 다시 시작하세요.";
        _log.Info($"관리자 권한 터미널 시작(별도 창): {shell.Executable} ({folder})");
    }

    /// <summary>The launcher, shell and paths for a new terminal, or null after telling the user what is missing.</summary>
    private (ITerminalLauncher Launcher, ShellLocation Shell, AppPaths Paths)? TerminalTarget()
    {
        if (_terminalLauncher is not { } launcher || _locateShell is not { } locate || _terminalPaths is not { } paths)
        {
            _dialogs.ShowError("터미널", "터미널 기능을 사용할 수 없습니다.");
            return null;
        }

        if (locate() is not { } shell)
        {
            _dialogs.ShowError("터미널", "PowerShell을 찾을 수 없습니다. pwsh.exe(PowerShell 7) 또는 Windows PowerShell이 필요합니다.");
            return null;
        }

        return (launcher, shell, paths);
    }

    /// <summary>
    /// Adds or removes the administrator prefix so a title tells the terminal's real rights (a restored tab runs with
    /// the rights of this run, which may differ from the run that saved it).
    /// </summary>
    internal static string WithPrivilege(string title, bool elevated)
    {
        var plain = title.StartsWith(AdministratorTitlePrefix, StringComparison.Ordinal) ? title[AdministratorTitlePrefix.Length..] : title;
        return elevated ? AdministratorTitlePrefix + plain : plain;
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

    /// <summary>Recreates terminal identities before AvalonDock resolves the saved layout; AI is never resumed.</summary>
    public async Task RestoreTerminalsAsync()
    {
        if (_terminalLauncher is not { } launcher || _locateShell is not { } locate || _terminalPaths is not { } paths)
        {
            return;
        }

        var saved = await Task.Run(_session.LoadTerminals).ConfigureAwait(true);
        if (saved.Count == 0) return;
        var shell = await Task.Run(locate).ConfigureAwait(true);
        if (shell is null)
        {
            StatusMessage = "PowerShell을 찾을 수 없어 지난 터미널을 복원하지 못했습니다.";
            return;
        }

        foreach (var state in saved)
        {
            if (Terminals.Any(t => t.ContentId == TerminalPaneViewModel.ContentIdPrefix + state.Id)) continue;
            try
            {
                var fallback = TerminalFolder(paths);
                var connection = await Task.Run(() =>
                {
                    var folder = Directory.Exists(state.WorkingDirectory) ? state.WorkingDirectory : fallback;
                    return new TerminalConnection(launcher.Start(ShellTerminal.Plan(shell, folder, paths), TerminalSize.Default), TerminalSize.Default, folder);
                }).ConfigureAwait(true);
                AddTerminal(new TerminalPaneViewModel(state.Id, WithPrivilege(state.Title, IsAdministrator), connection, CopyFromTerminal, _uiContext));
                _terminalNumber++;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.Warning("지난 터미널을 복원하지 못했습니다.", ex);
                StatusMessage = "일부 터미널을 복원하지 못했습니다. 자세한 내용은 출력 패널을 확인하세요.";
            }
        }
    }

    private void SaveTerminals() => _session.SaveTerminals(Terminals.Select(t => new TerminalSessionState(
        t.ContentId[TerminalPaneViewModel.ContentIdPrefix.Length..],
        t.PersistentTitle.Length > 200 ? t.PersistentTitle[..200] : t.PersistentTitle,
        t.Connection.WorkingDirectory ?? t.Session?.Scope.Primary.Root ?? TerminalFolder(Paths))).ToList());

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

    /// <summary>
    /// Pane a pane being added should open next to (the terminal whose "+" was clicked). The docking layout reads it
    /// while the pane is inserted; null lets the layout use the last active pane of the same kind.
    /// </summary>
    public PaneViewModel? PlacementAnchor { get; private set; }

    /// <summary>"+" on a terminal title: a new PowerShell terminal next to that terminal.</summary>
    private void OnNewTerminalRequested(object? sender, EventArgs e)
    {
        PlacementAnchor = sender as TerminalPaneViewModel;
        try
        {
            OpenTerminal();
        }
        finally
        {
            PlacementAnchor = null;
        }
    }

    internal void AddTerminal(TerminalPaneViewModel pane)
    {
        pane.CloseRequested += OnTerminalCloseRequested;
        pane.NewTerminalRequested += OnNewTerminalRequested;
        pane.Ended += (_, _) => OnPropertyChanged(nameof(RunningTerminalCount));
        pane.PropertyChanged += OnTerminalPropertyChanged;
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
                ? $"{pane.Title} 세션이 실행 중입니다.\n\n탭을 닫으면 세션이 중지됩니다. 변경 검토는 AI 모니터에서 할 수 있습니다. 닫을까요?"
                : $"{pane.Title}이(가) 실행 중입니다.\n\n탭을 닫으면 터미널과 그 안에서 실행한 프로그램이 모두 종료됩니다. 닫을까요?";
            if (pane.IsRunning && !_dialogs.Confirm("터미널", question))
            {
                pane.IsVisible = true;
                return;
            }

            CloseTerminal(pane);
        });
    }

    // A renamed tab is renamed in the AI 모니터 right away (not only on its next refresh).
    private void OnTerminalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalPaneViewModel.Title))
        {
            AiMonitor.Refresh();
        }
    }

    private void CloseTerminal(TerminalPaneViewModel pane)
    {
        pane.CloseRequested -= OnTerminalCloseRequested;
        pane.NewTerminalRequested -= OnNewTerminalRequested;
        pane.PropertyChanged -= OnTerminalPropertyChanged;
        Tools.Remove(pane);
        if (pane.Session is { } session)
        {
            // The manager owns the session's console and releases it after the exit (its replay stays available).
            _aiSessions?.Stop(session, "터미널 탭을 닫아 세션을 중지했습니다.");
            _aiSessions?.MarkClosed(session);
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
