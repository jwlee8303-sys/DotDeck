using System.IO;
using AvalonDock;
using AvalonDock.Layout;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Documents;
using DotDeck.Core.Explorer;
using DotDeck.Core.FileOperations;
using DotDeck.Core.Files;
using DotDeck.Core.Logging;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.SystemConfig;

namespace DotDeck.App.Tests;

public sealed class TerminalRestoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-restore-" + Guid.NewGuid().ToString("N"));
    private readonly List<ShellViewModel> _shells = [];
    private AppPaths Paths => new(_temp, HomeSource.CommandLine, Path.Combine(_temp, "roaming"), Path.Combine(_temp, "local"));
    private readonly Launcher _launcher = new();
    private readonly ShellLocation _shellLocation = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), true);

    public TerminalRestoreTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        foreach (var shell in _shells) shell.CloseAllTerminals();
        Directory.Delete(_temp, recursive: true);
    }

    private ShellViewModel CreateShell()
    {
        var paths = Paths;
        var store = PhysicalFileStore.Instance;
        var log = new FileAppLog(paths.LogsDir, TimeProvider.System);
        var registry = new ProjectRegistry(store, paths.ProjectsFile, TimeProvider.System, log);
        registry.Load();
        var backups = new BackupStore(store, paths.BackupsDir, TimeProvider.System);
        var shell = new ShellViewModel(new ShellServices(paths, log, store, registry,
            new SessionStateStore(store, paths.SessionFile, log), new ClipboardService(), new ShellLauncher(), new DialogService(),
            new FileOperationService(store, new WindowsRecycleBin(log), new FileOperationHistory(), backups),
            new TextFileReader(store), new SafeWriter(store, backups), backups,
            new FileEnvironmentStore(store, Path.Combine(_temp, "environment.json"))), "1.0.0", false);
        shell.AttachTerminals(_launcher, () => _shellLocation, paths);
        _shells.Add(shell);
        return shell;
    }

    [Fact]
    public async Task Closing_and_restarting_restores_the_same_terminal_identity_and_folder()
    {
        var first = CreateShell();
        first.OpenTerminalCommand.Execute(null);
        var original = Assert.Single(first.Terminals);
        first.SaveSession();
        first.CloseAllTerminals();

        var second = CreateShell();
        await second.RestoreTerminalsAsync();
        await second.RestoreTerminalsAsync(); // An accidental second call must not duplicate tabs.
        var restored = Assert.Single(second.Terminals);

        Assert.Equal(original.ContentId, restored.ContentId);
        Assert.Equal(original.PersistentTitle, restored.Title);
        Assert.Equal(_temp, restored.Connection.WorkingDirectory);
        Assert.True(restored.IsRunning);
        Assert.False(restored.IsAiSession);
        Assert.Equal(2, _launcher.Plans.Count);
        Assert.Equal(ShellTerminal.Plan(_shellLocation, _temp, Paths).Arguments, _launcher.Plans[^1].Arguments);
    }

    [Fact]
    public async Task An_ai_terminal_is_restored_as_an_idle_shell_without_restarting_ai()
    {
        using var fixture = new AiTerminalPaneTests();
        var aiPane = fixture.CreatePaneForView();
        var first = CreateShell();
        first.Tools.Add(aiPane);
        first.SaveSession();

        var second = CreateShell();
        await second.RestoreTerminalsAsync();
        var restored = Assert.Single(second.Terminals);

        Assert.Equal(aiPane.ContentId, restored.ContentId);
        Assert.Equal(aiPane.Connection.WorkingDirectory, restored.Connection.WorkingDirectory);
        Assert.Null(restored.Session);
        Assert.True(restored.IsRunning);
        Assert.Equal(_shellLocation.Executable, Assert.Single(_launcher.Plans).Executable);
    }

    [Fact]
    public async Task A_missing_saved_folder_falls_back_to_home()
    {
        var paths = Paths;
        var log = new FileAppLog(paths.LogsDir, TimeProvider.System);
        new SessionStateStore(PhysicalFileStore.Instance, paths.SessionFile, log)
            .SaveTerminals([new("t1", "PowerShell", Path.Combine(_temp, "deleted"))]);

        var shell = CreateShell();
        await shell.RestoreTerminalsAsync();

        Assert.Equal(_temp, Assert.Single(shell.Terminals).Connection.WorkingDirectory);
        Assert.Equal(_temp, Assert.Single(_launcher.Plans).WorkingDirectory);
    }

    [Fact]
    public async Task Last_and_named_layouts_keep_terminal_identity_and_position_after_restart()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var first = CreateShell();
                first.OpenTerminalCommand.Execute(null);
                var terminal = Assert.Single(first.Terminals);
                var manager = new DockingManager { Layout = DefaultLayout.Create() };
                var pane = manager.Layout.Descendents().OfType<LayoutAnchorablePane>().Single(p => p.Name == DefaultLayout.RightPaneName);
                pane.DockWidth = new System.Windows.GridLength(417);
                pane.Children.Add(new LayoutAnchorable { ContentId = terminal.ContentId, Content = terminal, Title = terminal.Title });
                var persistence = new LayoutPersistence(Paths.LayoutDir, new FileAppLog(Paths.LogsDir, TimeProvider.System));
                persistence.Save(manager);
                Assert.True(persistence.SaveNamed(manager, "작업 배치"));
                first.SaveSession();
                first.CloseAllTerminals();

                var second = CreateShell();
                second.RestoreTerminalsAsync().GetAwaiter().GetResult();
                var restoredManager = new DockingManager { Layout = DefaultLayout.Create() };
                Assert.Equal(LayoutRestoreResult.Restored, persistence.TryRestore(restoredManager, second));
                CheckPosition(restoredManager, terminal.ContentId, second);
                Assert.Equal(["작업 배치"], persistence.ListNamed());
                Assert.Equal(LayoutRestoreResult.Restored, persistence.TryLoadNamed(restoredManager, second, "작업 배치"));
                CheckPosition(restoredManager, terminal.ContentId, second);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Closing_a_tab_group_closes_ended_terminals_and_hides_tool_panes()
    {
        var shell = CreateShell();
        shell.OpenTerminalCommand.Execute(null);
        shell.OpenTerminalCommand.Execute(null);
        var terminals = shell.Terminals.ToList();
        Assert.Equal(2, terminals.Count);
        foreach (var terminal in terminals)
        {
            terminal.Connection.Terminate(); // ended terminals close without asking
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (terminals.Any(t => t.IsRunning))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "terminals did not end");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        shell.Output.IsVisible = true;
        shell.CloseTabs([.. terminals, shell.Output]);

        Assert.Empty(shell.Terminals);
        Assert.False(shell.Output.IsVisible);
        Assert.Contains(shell.Output, shell.Tools);
    }

    private static void CheckPosition(DockingManager manager, string id, ShellViewModel shell)
    {
        var terminal = Assert.Single(manager.Layout.Descendents().OfType<LayoutAnchorable>(), a => a.ContentId == id);
        Assert.Same(Assert.Single(shell.Terminals), terminal.Content);
        var pane = Assert.IsType<LayoutAnchorablePane>(terminal.Parent);
        Assert.Equal(DefaultLayout.RightPaneName, pane.Name);
        Assert.Equal(417, pane.DockWidth.Value);
    }

    private sealed class Launcher : ITerminalLauncher
    {
        public List<CliLaunchPlan> Plans { get; } = [];
        public ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size)
        {
            Plans.Add(plan);
            return new Terminal();
        }
    }

    private sealed class Terminal : ITerminalProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 1;
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public Task<int> Exited => _exit.Task;
        public void Resize(TerminalSize size) { }
        public IReadOnlyList<int> JobProcessIds() => [];
        public void Terminate(int exitCode = 1) => _exit.TrySetResult(exitCode);
        public void Dispose() { Input.Dispose(); Output.Dispose(); _exit.TrySetResult(0); }
    }
}
