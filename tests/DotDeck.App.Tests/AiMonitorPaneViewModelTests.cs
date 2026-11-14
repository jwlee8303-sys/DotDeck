using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.Tests;

public sealed class AiMonitorPaneViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-aimonitor-test-" + Guid.NewGuid().ToString("N"));
    private readonly Launcher _launcher = new();
    private readonly AiSessionManager _manager;
    private readonly Clock _time = new(DateTimeOffset.UtcNow);
    private readonly List<AiSession> _opened = [];

    public AiMonitorPaneViewModelTests()
    {
        var root = Path.Combine(_temp, "proj");
        Directory.CreateDirectory(root);
        var paths = new AppPaths(Path.Combine(_temp, "home"), HomeSource.CommandLine, Path.Combine(_temp, "roaming"), Path.Combine(_temp, "local"));
        ProjectDefinition[] projects = [new() { Id = "a", Kind = ProjectKind.Folder, Name = "Proj", Root = root }];
        _manager = new AiSessionManager(new AiSessionEnvironment
        {
            Paths = paths,
            Store = PhysicalFileStore.Instance,
            Projects = () => projects,
            Launcher = _launcher,
            Locate = cli => new CliLocatorResult(new CliLocation(cli, @"C:\tools\claude.exe", []), null),
            GuardExecutable = @"C:\Program Files\DotDeck\DotDeck.exe",
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
        });
    }

    public void Dispose()
    {
        _manager.Dispose();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AiMonitorPaneViewModel Pane()
    {
        var pane = new AiMonitorPaneViewModel(_opened.Add, _time) { IsVisible = false };
        pane.Attach(_manager);
        return pane;
    }

    private AiSession Start() => _manager.Start(new AiSessionStartRequest { PrimaryProjectId = "a", Prompt = "정리해줘" });

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "condition not reached");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void New_sessions_are_listed_newest_first_with_scope_and_summary()
    {
        var pane = Pane();
        Assert.False(pane.HasVisibleSessions);

        var first = Start();
        var second = Start();

        Assert.Equal([second, first], pane.Sessions.Select(s => s.Session));
        var item = pane.Sessions[0];
        Assert.Equal("Claude · 자동", item.KindText);
        Assert.Equal("Proj", item.ScopeText);
        Assert.Equal("실행 중", item.StateText);
        Assert.Equal("출력 없음", item.ActivityText);
        Assert.Equal("실행 중 2 · 작업 중 0 · 끝남 0", pane.Summary);
        Assert.True(pane.HasVisibleSessions);
    }

    [Fact]
    public async Task Output_marks_the_session_busy_until_it_goes_quiet()
    {
        var pane = Pane();
        Start();
        var item = Assert.Single(pane.Sessions);

        _launcher.Last.Write("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"읽는 중\"}]}}\n");
        await Eventually(() =>
        {
            pane.Refresh();
            return item.IsBusy;
        });
        Assert.Equal("작업 중", item.ActivityText);
        Assert.Equal("실행 중 1 · 작업 중 1 · 끝남 0", pane.Summary);

        _time.Advance(TimeSpan.FromSeconds(75));
        pane.Refresh();

        Assert.False(item.IsBusy);
        Assert.Equal("대기 · 1분 15초 전 출력", item.ActivityText);
    }

    [Fact]
    public async Task Finished_sessions_keep_their_elapsed_time_and_can_be_hidden()
    {
        var pane = Pane();
        var session = Start();
        var item = Assert.Single(pane.Sessions);

        _manager.Stop(session);
        await Eventually(() =>
        {
            pane.Refresh();
            return !item.IsRunning;
        });
        Assert.Equal("중지됨", item.StateText);
        Assert.Equal("중지됨", item.ActivityText);
        var elapsed = item.ElapsedText;

        _time.Advance(TimeSpan.FromMinutes(10));
        pane.Refresh();
        Assert.Equal(elapsed, item.ElapsedText);
        Assert.Equal("실행 중 0 · 작업 중 0 · 끝남 1", pane.Summary);

        pane.ShowFinished = false;
        Assert.False(item.IsShown);
        Assert.False(pane.HasVisibleSessions);

        pane.ShowFinished = true;
        Assert.True(item.IsShown);
    }

    [Fact]
    public void Open_passes_the_row_or_the_selected_session()
    {
        var pane = Pane();
        var first = Start();
        var second = Start();

        pane.OpenCommand.Execute(pane.Sessions[1]);
        Assert.Equal([first], _opened);

        pane.OpenCommand.Execute(null);
        Assert.Equal([first], _opened); // nothing selected

        pane.SelectedSession = pane.Sessions[0];
        pane.OpenCommand.Execute(null);
        Assert.Equal([first, second], _opened);
    }

    [Fact]
    public async Task Toolbar_actions_follow_the_selected_session_and_clear_removes_finished_rows()
    {
        var outputs = new List<AiSession>();
        var reviewed = new List<AiSession>();
        var confirmStop = false;
        var pane = new AiMonitorPaneViewModel(_opened.Add, _time, new AiMonitorActions(
            _ => throw new InvalidOperationException("no terminal"),
            outputs.Add,
            s =>
            {
                reviewed.Add(s);
                return Task.CompletedTask;
            },
            _ => confirmStop)) { IsVisible = false };
        pane.Attach(_manager);
        Assert.False(pane.StopCommand.CanExecute(null)); // nothing selected

        var session = Start();
        pane.SelectedSession = Assert.Single(pane.Sessions);
        Assert.False(pane.OpenTerminalCommand.CanExecute(null)); // non-interactive: no terminal tab

        pane.ShowOutputCommand.Execute(null);
        Assert.Equal([session], outputs);
        await pane.ReviewChangesCommand.ExecuteAsync(null);
        Assert.Equal([session], reviewed);

        pane.StopCommand.Execute(null);
        Assert.Equal(AiSessionState.Running, session.State); // declined

        confirmStop = true;
        pane.StopCommand.Execute(null);
        await Eventually(() => session.State != AiSessionState.Running);
        pane.Refresh();
        Assert.False(pane.StopCommand.CanExecute(null));

        var running = Start();
        pane.ClearFinishedCommand.Execute(null);
        Assert.Same(running, Assert.Single(pane.Sessions).Session);
        Assert.Null(pane.SelectedSession);

        // Later manager changes must not bring a cleared session back.
        var third = Start();
        Assert.Equal([third, running], pane.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void A_renamed_session_terminal_renames_its_row()
    {
        using var fixture = new AiTerminalPaneTests();
        var terminal = fixture.CreatePaneForView();
        var opened = new List<AiSession>();
        var pane = new AiMonitorPaneViewModel(_ => { }, _time, new AiMonitorActions(opened.Add, _ => { }, _ => Task.CompletedTask, _ => false))
        {
            IsVisible = false,
        };
        pane.Attach(fixture.Manager);
        pane.AttachTerminals(() => [terminal], _ => null, _ => { });
        var item = Assert.Single(pane.Sessions);
        Assert.Equal(terminal.Session!.Title, item.Title);

        terminal.BeginRename();
        terminal.RenameText = "리뷰 작업";
        terminal.CommitRename();
        pane.Refresh();

        Assert.Equal("리뷰 작업", item.Title);
        pane.SelectedSession = item;
        Assert.False(pane.ShowOutputCommand.CanExecute(null)); // interactive: the output is in the terminal
        pane.OpenTerminalCommand.Execute(null);
        Assert.Equal([terminal.Session], opened);
    }

    [Fact]
    public async Task Output_window_parses_buffered_and_live_output_and_masks_the_prompt()
    {
        var session = _manager.Start(new AiSessionStartRequest { PrimaryProjectId = "a", Prompt = "이 값으로 바꿔줘 API_KEY=fake-prompt-secret-1234" });
        _launcher.Last.Write("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"먼저 읽겠습니다\"}]}}\n");
        await Eventually(() => session.AttachOutput((_, _, _) => { }).Count == 1);

        var output = new DotDeck.App.ViewModels.Dialogs.AiSessionOutputViewModel(session, action => action());
        Assert.Equal("실행 중 · 가드 확인 대기", output.StateText);
        Assert.DoesNotContain("fake-prompt-secret-1234", output.Prompt, StringComparison.Ordinal);

        _launcher.Last.Write("{\"type\":\"result\",\"is_error\":false,\"num_turns\":2}\n");
        await Eventually(() => output.Lines.Count == 2);
        Assert.Equal([AiOutputKind.Text, AiOutputKind.Result], output.Lines.Select(l => l.Kind));
        Assert.Equal("먼저 읽겠습니다", output.Lines[0].Text);

        output.Detach();
        _launcher.Last.Write("{\"type\":\"result\",\"is_error\":false,\"num_turns\":3}\n");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(2, output.Lines.Count); // a closed window no longer follows the session
    }

    [Theory]
    [InlineData("claude", CliKind.Claude)]
    [InlineData("Claude.exe", CliKind.Claude)]
    [InlineData("codex", CliKind.Codex)]
    [InlineData("codex.exe", CliKind.Codex)]
    [InlineData("codex-command-runner", null)]
    [InlineData("node", null)]
    [InlineData("powershell", null)]
    [InlineData(null, null)]
    public void Process_names_map_to_their_cli(string? name, CliKind? expected) =>
        Assert.Equal(expected, AiMonitorPaneViewModel.ClassifyProcess(name));

    [Fact]
    public void Cli_started_in_a_terminal_is_listed_until_it_ends()
    {
        var process = new TerminalProcess();
        using var connection = new TerminalConnection(process, TerminalSize.Default, @"D:\work");
        var terminal = new TerminalPaneViewModel("t1", "Windows PowerShell", connection, _ => { }, ui: null);
        var started = _time.GetUtcNow().AddMinutes(-2);
        var names = new Dictionary<int, string> { [1] = "powershell", [2] = "claude", [3] = "claude", [4] = "rg" };
        var openedTerminals = new List<TerminalPaneViewModel>();
        var pane = Pane();
        pane.AttachTerminals(() => [terminal], id => names.TryGetValue(id, out var n) ? (n, id == 2 ? started : started.AddSeconds(5)) : null, openedTerminals.Add);

        Assert.Empty(pane.Sessions); // only the shell runs

        process.Ids = [1, 2, 3, 4]; // claude and a helper claude process: one row
        pane.Tick();
        var item = Assert.Single(pane.Sessions);
        Assert.Null(item.Session);
        Assert.Same(terminal, item.TerminalPane);
        Assert.Equal("Claude · Windows PowerShell", item.Title);
        Assert.Equal("Claude · 터미널", item.KindText);
        Assert.Equal(@"D:\work", item.ScopeText);
        Assert.Equal("직접 실행 · 가드 없음", item.GuardText);
        Assert.Equal("2분 0초", item.ElapsedText);
        Assert.True(item.IsRunning);

        pane.OpenCommand.Execute(item);
        Assert.Equal([terminal], openedTerminals);
        Assert.Empty(_opened);

        process.Ids = [1];
        pane.Tick();
        Assert.False(item.IsRunning);
        Assert.Equal("종료됨", item.ActivityText);
        Assert.Equal("실행 중 0 · 작업 중 0 · 끝남 1", pane.Summary);

        names[5] = "codex";
        process.Ids = [1, 5]; // started again: a new row
        pane.Tick();
        Assert.Equal(2, pane.Sessions.Count);
        Assert.Equal("Codex · 터미널", pane.Sessions[0].KindText);
        Assert.True(pane.Sessions[0].IsRunning);
    }

    [Fact]
    public void Process_inspector_reads_the_own_process()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var info = DotDeck.App.Infrastructure.Windows.ProcessInspector.Inspect(self.Id);

        Assert.NotNull(info);
        Assert.Equal(self.ProcessName, info.Value.Name, ignoreCase: true);
        Assert.NotNull(info.Value.StartedAt);
        Assert.Null(DotDeck.App.Infrastructure.Windows.ProcessInspector.Inspect(0));
    }

    [Fact]
    public void Terminal_output_marks_the_terminal_run_busy()
    {
        var process = new TerminalProcess { Ids = [2] };
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
        var pane = Pane();
        pane.AttachTerminals(() => [terminal], _ => ("codex", null), _ => { });
        var item = Assert.Single(pane.Sessions);
        Assert.Equal("출력 없음", item.ActivityText);

        process.Write("thinking…");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            pane.Refresh();
            return item.IsBusy;
        }, TimeSpan.FromSeconds(10)));
        Assert.Equal("작업 중", item.ActivityText);
    }

    [Theory]
    [InlineData(0, "0초")]
    [InlineData(59, "59초")]
    [InlineData(61, "1분 1초")]
    [InlineData(3725, "1시간 2분")]
    [InlineData(-5, "0초")]
    public void Spans_are_short(int seconds, string expected) =>
        Assert.Equal(expected, AiMonitorItemViewModel.FormatSpan(TimeSpan.FromSeconds(seconds)));

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }

    private sealed class TerminalProcess : ITerminalProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AnonymousPipeServerStream _output = new(PipeDirection.Out);

        public TerminalProcess() => Output = new AnonymousPipeClientStream(PipeDirection.In, _output.ClientSafePipeHandle);

        public IReadOnlyList<int> Ids { get; set; } = [1];

        public int ProcessId => 1;

        public Stream Output { get; }

        public Stream Input { get; } = new MemoryStream();

        public Task<int> Exited => _exit.Task;

        public void Write(string text) => _output.Write(Encoding.UTF8.GetBytes(text));

        public void Resize(TerminalSize size)
        {
        }

        public IReadOnlyList<int> JobProcessIds() => Ids;

        public void Terminate(int exitCode = 1)
        {
            _output.Dispose();
            _exit.TrySetResult(exitCode);
        }

        public void Dispose()
        {
            Terminate(-1);
            Output.Dispose();
        }
    }

    private sealed class Launcher : ICliProcessLauncher
    {
        public List<Process> Processes { get; } = [];

        public Process Last => Processes[^1];

        public ICliProcess Start(CliLaunchPlan plan)
        {
            var process = new Process();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class Process : ICliProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AnonymousPipeServerStream _stdout = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _stderr = new(PipeDirection.Out);

        public Process()
        {
            StandardOutput = new AnonymousPipeClientStream(PipeDirection.In, _stdout.ClientSafePipeHandle);
            StandardError = new AnonymousPipeClientStream(PipeDirection.In, _stderr.ClientSafePipeHandle);
        }

        public int ProcessId => 4321;

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public Task<int> Exited => _exit.Task;

        public void Write(string text) => _stdout.Write(Encoding.UTF8.GetBytes(text));

        public void Exit(int code)
        {
            _stdout.Dispose();
            _stderr.Dispose();
            _exit.TrySetResult(code);
        }

        public IReadOnlyList<int> JobProcessIds() => [];

        public void Terminate(int exitCode = 1) => Exit(exitCode);

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
