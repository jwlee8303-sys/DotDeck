using System.IO;
using AvalonDock.Layout;
using DotDeck.App.Docking;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.Services;
using DotDeck.App.ViewModels;
using DotDeck.App.ViewModels.Documents;
using DotDeck.App.ViewModels.Tools;
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

/// <summary>Where new panes open (same kind, project area), terminal "+" and in-place rename, extension window ids.</summary>
public sealed class PanePlacementTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-placement-" + Guid.NewGuid().ToString("N"));
    private readonly List<ShellViewModel> _shells = [];
    private readonly List<TerminalConnection> _connections = [];
    private readonly ShellLocation _shellLocation = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), true);

    public PanePlacementTests() => Directory.CreateDirectory(_temp);

    private AppPaths Paths => new(_temp, HomeSource.CommandLine, Path.Combine(_temp, "roaming"), Path.Combine(_temp, "local"));

    public void Dispose()
    {
        foreach (var shell in _shells) shell.CloseAllTerminals();
        foreach (var connection in _connections) connection.Dispose();
        Directory.Delete(_temp, recursive: true);
    }

    // ===== Placement rules =====

    [Fact]
    public Task First_terminal_joins_the_project_tab_group_and_later_ones_open_next_to_the_last_active() => OnSta(() =>
    {
        var shell = CreateShell();
        var (layout, strategy) = Layout(shell);
        var projects = Item(layout, shell.Projects);

        var first = Insert(layout, strategy, Terminal("1"));
        Assert.Same(projects.Parent, first.Parent);

        // Move the first terminal to the bottom group and use it: the next one opens right after it.
        var bottom = Pane(layout, DefaultLayout.BottomPaneName);
        Move(first, bottom, 0);
        var other = Insert(layout, strategy, Terminal("other"));
        Move(other, bottom, 1);
        strategy.NoteActive(first);
        var second = Insert(layout, strategy, Terminal("2"));
        Assert.Same(bottom, second.Parent);
        Assert.Equal(1, bottom.IndexOfChild(second));
    });

    [Fact]
    public Task Explicit_anchor_wins_and_a_closed_kind_reopens_where_it_last_was() => OnSta(() =>
    {
        var shell = CreateShell();
        var (layout, strategy) = Layout(shell);
        var right = Pane(layout, DefaultLayout.RightPaneName);
        var bottom = Pane(layout, DefaultLayout.BottomPaneName);
        var a = Insert(layout, strategy, Terminal("a"));
        Move(a, right, 0);
        var b = Insert(layout, strategy, Terminal("b"));
        Move(b, bottom, 0);
        strategy.NoteActive(b);

        // "+" on terminal a: next to a, although b was used last.
        strategy.ExplicitAnchor = () => a.Content;
        var c = Insert(layout, strategy, Terminal("c"));
        Assert.Same(right, c.Parent);
        strategy.ExplicitAnchor = null;

        // Every terminal closed: a new one opens in the group of the last active one.
        foreach (var t in new[] { a, b, c })
        {
            ((ILayoutContainer)t.Parent).RemoveChild(t);
        }

        bottom.Children.Add(new LayoutAnchorable { Content = shell.Output });
        var d = Insert(layout, strategy, Terminal("d"));
        Assert.Same(bottom, d.Parent);
    });

    [Fact]
    public Task Files_of_any_kind_open_next_to_the_last_active_file() => OnSta(() =>
    {
        var shell = CreateShell();
        var (layout, strategy) = Layout(shell);
        var main = layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().First();
        var split = new LayoutDocumentPane();
        ((LayoutPanel)main.Parent).Children.Add(split);

        var first = Insert(layout, strategy, Document(1));
        Assert.Same(main, first.Parent);

        var moved = Insert(layout, strategy, Document(2));
        Move(moved, split, 0);
        split.Children.Add(new LayoutDocument { Content = Document(3) });
        strategy.NoteActive(moved);
        var next = Insert(layout, strategy, Document(4));
        Assert.Same(split, next.Parent);
        Assert.Equal(1, split.IndexOfChild(next));
    });

    [Fact]
    public Task Building_a_layout_keeps_preferred_side_panes() => OnSta(() =>
    {
        var shell = CreateShell();
        var (layout, strategy) = Layout(shell);
        strategy.UsePreferredLocations = true;

        var terminal = new LayoutAnchorable { Content = Terminal("t") };
        Assert.True(strategy.BeforeInsertAnchorable(layout, terminal, null!));
        Assert.Equal(DefaultLayout.BottomPaneName, Assert.IsType<LayoutAnchorablePane>(terminal.Parent).Name);
        Assert.False(strategy.BeforeInsertDocument(layout, new LayoutDocument { Content = Document(1) }, null!));
    });

    [Fact]
    public Task A_hidden_project_area_leaves_the_first_terminal_to_its_preferred_pane() => OnSta(() =>
    {
        var shell = CreateShell();
        var (layout, strategy) = Layout(shell);
        Item(layout, shell.Projects).Hide();

        var terminal = Insert(layout, strategy, Terminal("t"));
        Assert.Equal(DefaultLayout.BottomPaneName, Assert.IsType<LayoutAnchorablePane>(terminal.Parent).Name);
    });

    [Fact]
    public void Every_document_is_one_file_kind()
    {
        Assert.Equal(PaneKinds.File, PaneKinds.Of(new ExtensionWindowDocumentViewModel(1, _ => { })));
        Assert.Equal(PaneKinds.Terminal, PaneKinds.Of(Terminal("t")));
        Assert.Equal(string.Empty, PaneKinds.Of(null));
    }

    [Fact]
    public void Extension_window_ids_round_trip()
    {
        var guide = new ExtensionWindowDocumentViewModel(3, _ => { });
        Assert.Equal(3, ExtensionWindowDocumentViewModel.NumberOf(guide.ContentId));
        Assert.Null(ExtensionWindowDocumentViewModel.NumberOf("doc:extension:x"));
        Assert.Null(ExtensionWindowDocumentViewModel.NumberOf("doc:extension:0"));
        Assert.Null(ExtensionWindowDocumentViewModel.NumberOf("doc:welcome"));
    }

    [Fact]
    public void Saved_extension_window_tabs_are_recreated_with_later_numbers_after_them()
    {
        var shell = CreateShell();
        var restored = Assert.IsType<ExtensionWindowDocumentViewModel>(shell.ResolveContent("doc:extension:4"));
        Assert.Contains(restored, shell.Documents);
        Assert.Equal(5, shell.AddExtensionWindowDocument().Number);
    }

    [Fact]
    public Task Extension_windows_open_with_a_guide_and_take_moved_files_and_terminals() => OnSta(() =>
    {
        var shell = CreateShell();
        var manager = new AvalonDock.DockingManager
        {
            LayoutUpdateStrategy = new LayoutInitializer { UsePreferredLocations = false },
            Layout = DefaultLayout.Create(),
        };
        manager.DocumentsSource = shell.Documents;
        manager.AnchorablesSource = shell.Tools;
        var window = new System.Windows.Window
        {
            Content = manager, Width = 800, Height = 600, ShowActivated = false, WindowStyle = System.Windows.WindowStyle.None, Left = -10000,
        };
        try
        {
            window.Show();
            var extensions = new ExtensionWindows(manager, shell);

            var first = extensions.Create();
            Assert.NotNull(first);
            var guide = Assert.Single(shell.Documents.OfType<ExtensionWindowDocumentViewModel>());
            Assert.Same(first, extensions.Find(guide)!.FindParent<LayoutDocumentFloatingWindow>());
            Assert.Equal(first, Assert.Single(extensions.List()));

            // A terminal (tool pane) moves in as a tab beside the guide.
            shell.OpenTerminalCommand.Execute(null);
            var terminal = extensions.Find(Assert.Single(shell.Terminals))!;
            Assert.True(extensions.MoveTo(terminal, first));
            Assert.Same(first, terminal.FindParent<LayoutDocumentFloatingWindow>());
            Assert.IsType<LayoutDocumentPane>(terminal.Parent);

            // "새 확장 창" for a file: the file fills the new window and no guide is added for it.
            var welcome = extensions.Find(Assert.Single(shell.Documents.OfType<WelcomeDocumentViewModel>()))!;
            var second = extensions.Create(welcome);
            Assert.NotNull(second);
            Assert.Same(second, welcome.FindParent<LayoutDocumentFloatingWindow>());
            Assert.Single(shell.Documents.OfType<ExtensionWindowDocumentViewModel>());
            Assert.Equal([first, second], extensions.List());
            Assert.StartsWith("확장 창 2", ExtensionWindows.NameOf(second!, 1), StringComparison.Ordinal);
        }
        finally
        {
            // The manager belongs to this thread: end the terminals and unbind here, not in Dispose.
            shell.CloseAllTerminals();
            manager.AnchorablesSource = null;
            manager.DocumentsSource = null;
            window.Close();
        }
    });

    // ===== Terminal title =====

    [Fact]
    public void Rename_changes_the_title_keeps_it_for_the_session_and_ignores_empty_input()
    {
        var pane = Terminal("t", "Windows PowerShell 2");

        pane.BeginRename();
        Assert.True(pane.IsRenaming);
        Assert.Equal("Windows PowerShell 2", pane.RenameText);
        pane.RenameText = "  빌드  ";
        pane.CommitRename();
        Assert.False(pane.IsRenaming);
        Assert.Equal("빌드", pane.Title);
        Assert.Equal("빌드", pane.PersistentTitle);

        pane.BeginRename();
        pane.RenameText = "   ";
        pane.CommitRename();
        Assert.Equal("빌드", pane.Title);

        pane.BeginRename();
        pane.RenameText = "취소됨";
        pane.CancelRename();
        Assert.Equal("빌드", pane.Title);

        pane.BeginRename();
        pane.RenameText = new string('가', TerminalPaneViewModel.MaxTitleLength + 10);
        pane.CommitRename();
        Assert.Equal(TerminalPaneViewModel.MaxTitleLength, pane.Title.Length);
    }

    [Fact]
    public async Task A_renamed_terminal_keeps_its_name_after_restart_and_marks_the_exit()
    {
        var first = CreateShell();
        first.OpenTerminalCommand.Execute(null);
        var pane = Assert.Single(first.Terminals);
        pane.BeginRename();
        pane.RenameText = "서버";
        pane.CommitRename();
        first.SaveSession();
        first.CloseAllTerminals();

        var second = CreateShell();
        await second.RestoreTerminalsAsync();
        var restored = Assert.Single(second.Terminals);
        Assert.Equal("서버", restored.Title);

        restored.Connection.Dispose();
        await WaitUntil(() => !restored.IsRunning);
        Assert.Equal("서버 (종료됨)", restored.Title);
    }

    [Fact]
    public void Plus_opens_a_numbered_terminal_anchored_next_to_the_clicked_one()
    {
        var shell = CreateShell();
        shell.OpenTerminalCommand.Execute(null);
        var first = Assert.Single(shell.Terminals);
        object? anchorWhileAdding = null;
        shell.Tools.CollectionChanged += (_, _) => anchorWhileAdding = shell.PlacementAnchor;

        first.NewTerminalCommand.Execute(null);

        Assert.Equal(2, shell.Terminals.Count());
        Assert.Same(first, anchorWhileAdding);
        Assert.Null(shell.PlacementAnchor);
        Assert.Equal(_shellLocation.DisplayName + " 2", shell.Terminals.Last().Title);
        Assert.True(first.CanOpenTerminalHere);
    }

    // ===== Administrator terminals =====

    [Fact]
    public void An_elevated_app_opens_administrator_terminals_in_tabs_with_the_prefix()
    {
        var launcher = new RecordingShellLauncher(accept: true);
        var shell = CreateShell(administrator: true, launcher: launcher);

        shell.OpenAdministratorTerminalCommand.Execute(null);
        shell.OpenTerminalCommand.Execute(null);

        Assert.Equal(
            ["관리자: " + _shellLocation.DisplayName, "관리자: " + _shellLocation.DisplayName + " 2"],
            shell.Terminals.Select(t => t.Title));
        Assert.Empty(launcher.Elevated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_normal_app_opens_an_elevated_window_instead_of_a_tab(bool accepted)
    {
        var launcher = new RecordingShellLauncher(accepted);
        var shell = CreateShell(launcher: launcher);

        shell.OpenAdministratorTerminalCommand.Execute(null);

        Assert.Empty(shell.Terminals);
        var (exe, folder) = Assert.Single(launcher.Elevated);
        Assert.Equal(_shellLocation.Executable, exe);
        Assert.True(Directory.Exists(folder));
        Assert.Contains(accepted ? "별도 창" : "취소", shell.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Normal_terminals_of_a_normal_app_have_no_prefix()
    {
        var shell = CreateShell();
        shell.OpenTerminalCommand.Execute(null);
        Assert.Equal(_shellLocation.DisplayName, Assert.Single(shell.Terminals).Title);
    }

    [Fact]
    public async Task A_restored_terminal_title_follows_the_rights_of_this_run()
    {
        var elevated = CreateShell(administrator: true);
        elevated.OpenTerminalCommand.Execute(null);
        elevated.SaveSession();
        elevated.CloseAllTerminals();

        var normal = CreateShell();
        await normal.RestoreTerminalsAsync();
        Assert.Equal(_shellLocation.DisplayName, Assert.Single(normal.Terminals).Title);
        normal.SaveSession();
        normal.CloseAllTerminals();

        var again = CreateShell(administrator: true);
        await again.RestoreTerminalsAsync();
        Assert.Equal("관리자: " + _shellLocation.DisplayName, Assert.Single(again.Terminals).Title);
    }

    [Theory]
    [InlineData("Windows PowerShell", true, "관리자: Windows PowerShell")]
    [InlineData("관리자: Windows PowerShell 2", true, "관리자: Windows PowerShell 2")]
    [InlineData("관리자: 서버", false, "서버")]
    [InlineData("서버", false, "서버")]
    public void WithPrivilege_adds_or_removes_the_prefix_once(string title, bool elevated, string expected) =>
        Assert.Equal(expected, ShellViewModel.WithPrivilege(title, elevated));

    [Fact]
    public void The_elevated_start_command_keeps_the_folder_as_one_literal()
    {
        var encoded = ShellLauncher.ElevatedStartCommand(@"C:\it's; $(x)");
        var command = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(@"Set-Location -LiteralPath 'C:\it''s; $(x)'", command);
    }

    // ===== Helpers =====

    private static (LayoutRoot Layout, LayoutInitializer Strategy) Layout(ShellViewModel shell)
    {
        var layout = DefaultLayout.Create();
        Pane(layout, DefaultLayout.LeftPaneName).Children.Add(new LayoutAnchorable { Content = shell.Projects });
        return (layout, new LayoutInitializer { UsePreferredLocations = false });
    }

    private static LayoutContent Insert(LayoutRoot layout, LayoutInitializer strategy, object content)
    {
        LayoutContent item = content is DocumentViewModel ? new LayoutDocument { Content = content } : new LayoutAnchorable { Content = content };
        var placed = item is LayoutDocument document
            ? strategy.BeforeInsertDocument(layout, document, null!)
            : strategy.BeforeInsertAnchorable(layout, (LayoutAnchorable)item, null!);
        Assert.True(placed);
        return item;
    }

    private static void Move(LayoutContent item, ILayoutPane target, int index)
    {
        ((ILayoutContainer)item.Parent).RemoveChild(item);
        switch (target)
        {
            case LayoutAnchorablePane tools:
                tools.Children.Insert(index, (LayoutAnchorable)item);
                break;
            case LayoutDocumentPane documents:
                documents.Children.Insert(index, item);
                break;
        }
    }

    private static LayoutAnchorablePane Pane(LayoutRoot layout, string name) =>
        layout.Descendents().OfType<LayoutAnchorablePane>().Single(p => p.Name == name);

    private static LayoutAnchorable Item(LayoutRoot layout, object content) =>
        layout.Descendents().OfType<LayoutAnchorable>().Single(a => ReferenceEquals(a.Content, content));

    private static ExtensionWindowDocumentViewModel Document(int number) => new(number, _ => { });

    private TerminalPaneViewModel Terminal(string id, string title = "PowerShell")
    {
        var connection = new TerminalConnection(new FakeTerminal(), TerminalSize.Default, _temp);
        _connections.Add(connection);
        return new TerminalPaneViewModel(id, title, connection, _ => { }, ui: null);
    }

    private ShellViewModel CreateShell(bool administrator = false, IShellLauncher? launcher = null)
    {
        var paths = Paths;
        var store = PhysicalFileStore.Instance;
        var log = new FileAppLog(paths.LogsDir, TimeProvider.System);
        var registry = new ProjectRegistry(store, paths.ProjectsFile, TimeProvider.System, log);
        registry.Load();
        var backups = new BackupStore(store, paths.BackupsDir, TimeProvider.System);
        var shell = new ShellViewModel(new ShellServices(paths, log, store, registry,
            new SessionStateStore(store, paths.SessionFile, log), new ClipboardService(), launcher ?? new ShellLauncher(), new DialogService(),
            new FileOperationService(store, new WindowsRecycleBin(log), new FileOperationHistory(), backups),
            new TextFileReader(store), new SafeWriter(store, backups), backups,
            new FileEnvironmentStore(store, Path.Combine(_temp, "environment.json"))), "1.0.0", administrator);
        shell.AttachTerminals(new FakeLauncher(), () => _shellLocation, paths);
        _shells.Add(shell);
        return shell;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "condition not reached");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static async Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                test();
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

    private sealed class RecordingShellLauncher(bool accept) : IShellLauncher
    {
        public List<(string Shell, string Directory)> Elevated { get; } = [];
        public void RevealInExplorer(string path) { }
        public bool OpenInVsCode(string path) => false;
        public void OpenTerminal(string directory) { }
        public bool OpenElevatedTerminal(string shellExecutable, string directory)
        {
            Elevated.Add((shellExecutable, directory));
            return accept;
        }
    }

    private sealed class FakeLauncher : ITerminalLauncher
    {
        public ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size) => new FakeTerminal();
    }

    private sealed class FakeTerminal : ITerminalProcess
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
