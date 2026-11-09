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

/// <summary>Terminal tabs (SPEC 11.3-4): plain PowerShell terminals in a pseudo console, one Job per tab.</summary>
public sealed partial class ShellViewModel
{
    private ITerminalLauncher? _terminalLauncher;
    private Func<ShellLocation?>? _locateShell;
    private AppPaths? _terminalPaths;
    private int _terminalNumber;

    public IEnumerable<TerminalPaneViewModel> Terminals => Tools.OfType<TerminalPaneViewModel>();

    public int RunningTerminalCount => Terminals.Count(t => t.IsRunning);

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

            if (pane.IsRunning && !_dialogs.Confirm("터미널", $"{pane.Title}이(가) 실행 중입니다.\n\n탭을 닫으면 터미널과 그 안에서 실행한 프로그램이 모두 종료됩니다. 닫을까요?"))
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
        pane.Connection.Dispose();
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
