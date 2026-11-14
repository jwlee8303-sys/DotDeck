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
