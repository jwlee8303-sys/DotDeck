using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotDeck.App.Services;
using DotDeck.Core.Editors;
using DotDeck.Core.Hooks;

namespace DotDeck.App.ViewModels.Tools;

/// <summary>
/// Hook test runner (SPEC §7): runs a hook command with an editable sample event JSON on stdin and shows stdout,
/// stderr, exit code and duration. Every run is confirmed first; the timeout is 10 seconds.
/// </summary>
public sealed partial class HookTestPaneViewModel : ToolPaneViewModel
{
    private CancellationTokenSource? _run;

    public HookTestPaneViewModel()
        : base("tool.hooktest", "훅 테스트", ToolLocation.Bottom)
    {
    }

    /// <summary>Set by the shell (dialogs, working folder) once services exist.</summary>
    public IDialogService? Dialogs { get; set; }

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    public IReadOnlyList<string> Events => Profile.Events;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Events))]
    private HookTestProfile _profile = ClaudeHookProfile.Default;

    [ObservableProperty]
    private string _event = "PreToolUse";

    [ObservableProperty]
    private string _matcher = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _output = "설정 폼의 훅 행에서 ▷ 버튼을 누르거나, 명령을 입력하고 실행하세요.";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;

    /// <summary>Loads a hook (from a settings form row) with a fresh sample input; does not run it.</summary>
    public void Load(string evt, string matcher, string command, HookTestProfile? profile = null)
    {
        Profile = profile ?? ClaudeHookProfile.Default;
        Event = evt;
        Matcher = matcher;
        Command = command;
        ResetSample();
        ResultText = string.Empty;
        Output = "입력 JSON을 확인한 뒤 실행하세요.";
    }

    [RelayCommand]
    private void ResetSample() => Input = Profile.Sample(Event, Matcher, WorkingDirectory);

    private bool CanRun() => !IsRunning && Command.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Dialogs is { } dialogs
            && !dialogs.Confirm("훅 테스트 실행", $"다음 명령을 실행합니다(최대 {HookRunner.DefaultTimeout.TotalSeconds:0}초).\n\n{Command}\n\n작업 폴더: {WorkingDirectory}\n\n실행할까요?"))
        {
            return;
        }

        IsRunning = true;
        ResultText = "실행 중…";
        Output = string.Empty;
        _run = new CancellationTokenSource();
        try
        {
            var env = Profile.Environment(WorkingDirectory);
            var result = await HookRunner.RunAsync(Command, Input, WorkingDirectory, HookRunner.DefaultTimeout, env, _run.Token).ConfigureAwait(true);
            ResultText = $"{result.Meaning} · 종료 코드 {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"} · {result.Elapsed.TotalMilliseconds:0} ms";
            Output = Core.Security.SecretMasker.MaskText($"[stdout]\n{result.StandardOutput}\n[stderr]\n{result.StandardError}");
        }
        catch (OperationCanceledException)
        {
            ResultText = "중지했습니다.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ResultText = "실행하지 못했습니다.";
            Output = ex.Message;
        }
        finally
        {
            _run.Dispose();
            _run = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _run?.Cancel();
}
