using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;

namespace DotDeck.App.Tests;

/// <summary>The terminal tab of an interactive AI session: scope chips, exclusion, stop, restart and state text.</summary>
public sealed class AiTerminalPaneTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-aiterm-test-" + Guid.NewGuid().ToString("N"));
    private readonly Terminals _terminals = new();
    private readonly AiSessionManager _manager;
    private readonly List<string> _log = [];

    public AiTerminalPaneTests()
    {
        var main = Path.Combine(_temp, "main");
        var web = Path.Combine(_temp, "web");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(web);
        var paths = new AppPaths(Path.Combine(_temp, "home"), HomeSource.CommandLine, Path.Combine(_temp, "roaming"), Path.Combine(_temp, "local"));
        ProjectDefinition[] projects =
        [
            new() { Id = "a", Kind = ProjectKind.Folder, Name = "DotDeck", Root = main },
            new() { Id = "b", Kind = ProjectKind.Folder, Name = "웹게임", Root = web },
        ];
        _manager = new AiSessionManager(new AiSessionEnvironment
        {
            Paths = paths,
            Store = PhysicalFileStore.Instance,
            Projects = () => projects,
            Launcher = new NoPipeLauncher(),
            TerminalLauncher = _terminals,
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

    private (AiSession Session, TerminalPaneViewModel Pane) Start()
    {
        var session = _manager.Start(new AiSessionStartRequest { PrimaryProjectId = "a", AdditionalProjectIds = ["b"], Mode = LaunchMode.Interactive });
        var pane = new TerminalPaneViewModel(session, new AiTerminalActions(
            s => { _log.Add("review " + s.Id); return Task.CompletedTask; },
            s => { _log.Add("stop"); _manager.Stop(s); },
            s => { _log.Add("restart"); return Task.CompletedTask; },
            (s, id) => { _log.Add("exclude " + id); return _manager.ExcludeProject(s, id); },
            text => _log.Add("copy " + text)), ui: null);
        return (session, pane);
    }

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
    public void Tab_shows_the_scope_as_chips_and_the_guard_state()
    {
        var (session, pane) = Start();

        Assert.True(pane.IsAiSession);
        Assert.Equal("tool.terminal:" + session.Id, pane.ContentId);
        Assert.Equal("Claude · DotDeck (+웹게임)", pane.Title);
        Assert.Equal([("DotDeck", true), ("웹게임", false)], pane.Chips.Select(c => (c.Name, c.IsPrimary)));
        Assert.False(pane.Chips[0].CanExclude);
        Assert.True(pane.Chips[1].CanExclude);
        Assert.Equal("가드 확인 대기", pane.SessionState);

        File.WriteAllText(Path.Combine(session.Directory, "heartbeat"), "x");
        Assert.True(SpinUntil(() => session.GuardConnected));
        pane.RefreshSession();
        Assert.Equal("가드 적용됨", pane.SessionState);
    }

    [Fact]
    public void Chip_copies_its_path_and_excluding_narrows_the_scope()
    {
        var (session, pane) = Start();
        var web = pane.Chips[1];

        web.CopyPathCommand.Execute(null);
        web.ExcludeCommand.Execute(null);

        Assert.Equal(["copy " + web.Root, "exclude b"], _log);
        Assert.Empty(session.Scope.Additional);
        Assert.Equal(["DotDeck"], pane.Chips.Select(c => c.Name));
        Assert.Equal("Claude · DotDeck", pane.Title);

        pane.Chips[0].ExcludeCommand.Execute(null); // the primary chip never asks the shell
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public async Task Stop_and_exit_update_the_tab()
    {
        var (session, pane) = Start();
        Assert.True(pane.StopCommand.CanExecute(null));

        pane.StopCommand.Execute(null);

        await Eventually(() => !pane.IsRunning);
        Assert.Equal(AiSessionState.Stopped, session.State);
        Assert.StartsWith("중지됨", pane.SessionState, StringComparison.Ordinal);
        Assert.False(pane.StopCommand.CanExecute(null));
        Assert.False(pane.Chips[1].CanExclude);
        Assert.EndsWith("(종료됨)", pane.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_and_restart_go_to_the_shell()
    {
        var (session, pane) = Start();

        await pane.ReviewChangesCommand.ExecuteAsync(null);
        await pane.RestartWithProjectsCommand.ExecuteAsync(null);

        Assert.Equal(["review " + session.Id, "restart"], _log);
    }

    [Fact]
    public void Plain_terminals_have_no_session_actions()
    {
        var (_, aiPane) = Start();
        var plain = new TerminalPaneViewModel("p", "PowerShell", aiPane.Connection, _ => { }, ui: null);

        Assert.False(plain.IsAiSession);
        Assert.Empty(plain.Chips);
        Assert.False(plain.StopCommand.CanExecute(null));
        Assert.False(plain.RestartWithProjectsCommand.CanExecute(null));
    }

    internal TerminalPaneViewModel CreatePaneForView() => Start().Pane;

    private static bool SpinUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));

    private sealed class NoPipeLauncher : ICliProcessLauncher
    {
        public ICliProcess Start(CliLaunchPlan plan) => throw new InvalidOperationException("interactive only");
    }

    private sealed class Terminals : ITerminalLauncher
    {
        public ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size) => new Terminal();
    }

    private sealed class Terminal : ITerminalProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AnonymousPipeServerStream _output = new(PipeDirection.Out);

        public Terminal() => Output = new AnonymousPipeClientStream(PipeDirection.In, _output.ClientSafePipeHandle);

        public int ProcessId => 4321;

        public Stream Output { get; }

        public Stream Input { get; } = new MemoryStream();

        public Task<int> Exited => _exit.Task;

        public void Resize(TerminalSize size)
        {
        }

        public IReadOnlyList<int> JobProcessIds() => [];

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
}

/// <summary>The terminal view's XAML loads and binds the AI toolbar (no window, so no WebView is created).</summary>
public sealed class TerminalViewXamlTests
{
    [Fact]
    public async Task Ai_toolbar_shows_one_chip_per_scope_project()
    {
        using var fixture = new AiTerminalPaneTests();
        var pane = fixture.CreatePaneForView();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new DotDeck.App.Views.Tools.TerminalView();
                foreach (var file in new[] { "Dark.xaml", "Metrics.xaml", "Controls.xaml" })
                {
                    view.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"/DotDeck;component/Themes/{file}", UriKind.Relative),
                    });
                }

                view.DataContext = pane;
                view.Measure(new System.Windows.Size(900, 300));
                view.Arrange(new System.Windows.Rect(0, 0, 900, 300));
                view.UpdateLayout();

                var buttons = Descendants(view).OfType<System.Windows.Controls.Button>().ToList();
                Assert.Equal(["DotDeck", "웹게임"], buttons.Where(b => b.DataContext is ScopeChipViewModel).Select(b => (string)b.Content));
                Assert.Contains(buttons, b => b.Command == pane.StopCommand);
                Assert.Contains(buttons, b => b.Command == pane.ReviewChangesCommand);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
