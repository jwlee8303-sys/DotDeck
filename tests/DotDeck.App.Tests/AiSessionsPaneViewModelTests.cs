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

public sealed class AiSessionsPaneViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-aipane-test-" + Guid.NewGuid().ToString("N"));
    private readonly Launcher _launcher = new();
    private readonly AiSessionManager _manager;
    private readonly List<AiSession> _reviewed = [];
    private bool _confirmStop = true;

    public AiSessionsPaneViewModelTests()
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

    private AiSessionsPaneViewModel Pane()
    {
        var pane = new AiSessionsPaneViewModel(s =>
        {
            _reviewed.Add(s);
            return Task.CompletedTask;
        }, _ => _confirmStop) { IsVisible = false };
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
    public async Task New_session_appears_selected_with_buffered_and_live_output_parsed()
    {
        var session = Start();
        _launcher.Last.Write("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"먼저 읽겠습니다\"}]}}\n");
        await Eventually(() => session.AttachOutput((_, _, _) => { }).Count == 1);

        var pane = Pane(); // attaches after the first line was written

        var item = Assert.Single(pane.Sessions);
        Assert.Same(item, pane.SelectedSession);
        Assert.True(pane.IsVisible);
        Assert.Equal("실행 중 · 가드 확인 대기", item.StateText);

        _launcher.Last.Write("{\"type\":\"result\",\"is_error\":false,\"num_turns\":2}\n");
        await Eventually(() => item.Lines.Count == 2);
        Assert.Equal([AiOutputKind.Text, AiOutputKind.Result], item.Lines.Select(l => l.Kind));
        Assert.Equal("먼저 읽겠습니다", item.Lines[0].Text);
    }

    [Fact]
    public async Task Stop_asks_first_and_state_follows_the_session()
    {
        var pane = Pane();
        var session = Start();
        var item = Assert.Single(pane.Sessions);

        _confirmStop = false;
        item.StopCommand.Execute(null);
        Assert.Equal(AiSessionState.Running, session.State);

        _confirmStop = true;
        item.StopCommand.Execute(null);
        await Eventually(() => item.StateText == "중지됨");
        Assert.False(item.IsRunning);
        Assert.False(item.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Review_and_clear_finished()
    {
        var pane = Pane();
        Start();
        var running = Start();
        var first = pane.Sessions[1];
        _launcher.Processes[0].Exit(0);
        await Eventually(() => !first.IsRunning);

        await first.ReviewChangesCommand.ExecuteAsync(null);
        Assert.Single(_reviewed);

        pane.ClearFinishedCommand.Execute(null);

        Assert.Same(running, Assert.Single(pane.Sessions).Session);

        // Later manager changes must not bring a cleared session back.
        var third = Start();
        Assert.Equal([third, running], pane.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void Prompt_is_shown_with_secrets_masked()
    {
        var pane = Pane();
        _manager.Start(new AiSessionStartRequest { PrimaryProjectId = "a", Prompt = "이 값으로 바꿔줘 API_KEY=fake-prompt-secret-1234" });

        var item = Assert.Single(pane.Sessions);

        Assert.DoesNotContain("fake-prompt-secret-1234", item.Prompt, StringComparison.Ordinal);
        Assert.Contains("이 값으로 바꿔줘", item.Prompt, StringComparison.Ordinal);
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
