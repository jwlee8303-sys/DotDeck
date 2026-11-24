using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.ContextBudget;
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
    [InlineData("agy", CliKind.Antigravity)]
    [InlineData("AGY.exe", CliKind.Antigravity)]
    [InlineData("codex-command-runner", null)]
    [InlineData("node", null)]
    [InlineData("powershell", null)]
    [InlineData(null, null)]
    public void Process_names_map_to_their_cli(string? name, CliKind? expected) =>
        Assert.Equal(expected, AiMonitorPaneViewModel.ClassifyProcess(name));

    [Fact]
    public void Agy_started_in_a_terminal_is_listed()
    {
        var process = new TerminalProcess();
        using var connection = new TerminalConnection(process, TerminalSize.Default, @"D:\work");
        var terminal = new TerminalPaneViewModel("t1", "PowerShell", connection, _ => { }, ui: null);
        var names = new Dictionary<int, string> { [1] = "pwsh", [2] = "agy" };
        var pane = Pane();
        pane.AttachTerminals(() => [terminal], id => names.TryGetValue(id, out var n) ? (n, _time.GetUtcNow()) : null, _ => { });

        process.Ids = [1, 2];
        pane.Tick();
        var item = Assert.Single(pane.Sessions);
        Assert.Equal(CliKind.Antigravity, item.Cli);
        Assert.Equal("agy · PowerShell", item.Title);
        Assert.Equal("agy · 터미널", item.KindText);
        Assert.True(item.IsRunning);

        process.Ids = [1];
        pane.Tick();
        Assert.True(item.IsRunning); // within the grace period
        _time.Advance(AiMonitorPaneViewModel.ProcessGrace);
        pane.Tick();
        Assert.False(item.IsRunning);
    }

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
        _time.Advance(AiMonitorPaneViewModel.ProcessGrace);
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
    public void Codex_ends_when_its_server_is_still_running_and_Claude_starts()
    {
        var executable = Path.Combine(_temp, "codex.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("app-server");
        using var server = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.Equal(Environment.ProcessId, ProcessInspector.ParentId(server.Id));
            var process = new TerminalProcess { Ids = [server.Id] };
            using var connection = new TerminalConnection(process, TerminalSize.Default);
            var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
            var pane = Pane();
            var started = _time.GetUtcNow();
            pane.AttachTerminals(() => [terminal], id => id switch
            {
                2 => ("codex", started),
                3 => ("claude", started),
                _ => ProcessInspector.Inspect(id),
            }, _ => { });
            Assert.Empty(pane.Sessions);

            process.Ids = [2, server.Id];
            pane.Tick();
            var codex = Assert.Single(pane.Sessions);
            Assert.True(codex.IsRunning);

            process.Ids = [3, server.Id];
            _time.Advance(AiMonitorPaneViewModel.ProcessGrace);
            pane.Tick();

            Assert.False(server.HasExited);
            Assert.False(codex.IsRunning);
            Assert.Equal(CliKind.Claude, Assert.Single(pane.Sessions, item => item.IsRunning).Cli);
        }
        finally
        {
            server.StandardInput.Close();
            if (!server.WaitForExit(3000))
            {
                server.Kill();
                server.WaitForExit();
            }
        }
    }

    [Fact]
    public void A_launcher_or_self_update_that_replaces_the_process_keeps_the_same_row()
    {
        // agy updating itself: the first agy.exe starts the new one and exits; a gap between scans is bridged too.
        var process = new TerminalProcess { Ids = [1, 2] };
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
        var names = new Dictionary<int, string> { [1] = "powershell", [2] = "agy", [3] = "agy" };
        var pane = Pane();
        var initial = _time.GetUtcNow();
        pane.AttachTerminals(() => [terminal], id => names.TryGetValue(id, out var n) ? (n, id == 3 ? initial.AddSeconds(16) : initial) : null,
            _ => { }, parentOf: id => id == 1 ? null : 1);
        var item = Assert.Single(pane.Sessions);

        for (var second = 0; second < 14; second++) // scanned every second while in use
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            pane.Tick();
        }

        process.Ids = [1]; // the old one ended, the new one not started yet
        _time.Advance(TimeSpan.FromSeconds(1));
        pane.Tick();
        _time.Advance(TimeSpan.FromSeconds(1));
        process.Ids = [1, 3];
        pane.Tick();

        Assert.Same(item, Assert.Single(pane.Sessions));
        Assert.True(item.IsRunning);
        Assert.Equal([3], item.TerminalProcesses!.Select(p => p.Id));

        for (var second = 0; second < 60; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            pane.Tick();
        }

        Assert.True(item.IsRunning); // still in use

        process.Ids = [1]; // /quit
        pane.Tick();
        _time.Advance(AiMonitorPaneViewModel.ProcessGrace);
        pane.Tick();
        Assert.False(item.IsRunning);
        Assert.Equal("종료됨", item.ActivityText);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("agy")]
    public void The_same_cli_relaunched_at_the_grace_boundary_gets_a_new_row(string cli)
    {
        var process = new TerminalProcess { Ids = [1, 2] };
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
        var pane = Pane();
        var started = _time.GetUtcNow();
        var restarted = started + AiMonitorPaneViewModel.ProcessGrace;
        pane.AttachTerminals(() => [terminal], id => id == 1 ? null : (cli, id == 2 ? started : restarted),
            _ => { }, parentOf: id => id == 1 ? null : 1);
        var old = Assert.Single(pane.Sessions);

        process.Ids = [1];
        _time.Advance(TimeSpan.FromSeconds(2));
        pane.Tick();
        Assert.True(old.IsRunning);

        process.Ids = [1, 3];
        _time.Advance(TimeSpan.FromSeconds(1));
        pane.Tick();
        Assert.False(old.IsRunning);
        var fresh = Assert.Single(pane.Sessions, row => row.IsRunning);
        Assert.NotSame(old, fresh);
        Assert.Equal(restarted, fresh.StartedAt);
    }

    [Fact]
    public void Two_identical_clis_in_one_terminal_have_independent_rows_and_helpers()
    {
        var process = new TerminalProcess { Ids = [1, 2, 3, 4, 5] };
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
        var pane = Pane();
        var started = _time.GetUtcNow().AddSeconds(-10);
        pane.AttachTerminals(() => [terminal], id => id is 2 or 4 or 5 ? ("claude", started.AddSeconds(id)) : null,
            _ => { }, parentOf: id => id switch { 1 => null, 4 => 3, 3 => 2, _ => 1 });
        Assert.Equal(2, pane.Sessions.Count);
        var first = Assert.Single(pane.Sessions, row => row.TerminalProcesses!.Any(p => p.Id == 2));
        var second = Assert.Single(pane.Sessions, row => row.TerminalProcesses!.Any(p => p.Id == 5));
        Assert.Equal([2, 4], first.TerminalProcesses!.Select(p => p.Id).Order());

        process.Ids = [1, 5];
        _time.Advance(AiMonitorPaneViewModel.ProcessGrace);
        pane.Tick();
        Assert.False(first.IsRunning);
        Assert.Same(second, Assert.Single(pane.Sessions, row => row.IsRunning));
    }

    [Fact]
    public void Process_inspector_reads_the_own_process()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var info = ProcessInspector.Inspect(self.Id);

        Assert.NotNull(info);
        Assert.Equal(self.ProcessName, info.Value.Name, ignoreCase: true);
        Assert.NotNull(info.Value.StartedAt);
        Assert.Null(ProcessInspector.Inspect(0));
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

    private sealed class CountingHost : DotDeck.Core.AiCli.ContextBudget.IMcpProcessHost
    {
        public int Starts { get; private set; }

        public DotDeck.Core.AiCli.ContextBudget.IMcpProcess Start(string executable, string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            Starts++;
            throw new IOException("not started in tests");
        }
    }

    private (AiMonitorPaneViewModel Pane, CountingHost Host, List<string> Confirms) ContextPane(bool directMeasure)
    {
        var home = Path.Combine(_temp, "home");
        var host = new CountingHost();
        var confirms = new List<string>();
        var pane = Pane();
        pane.AttachContext(new ContextBudgetViewModel(new ContextBudgetServices(
            new DotDeck.Core.AiCli.ContextBudget.ContextCollector(home),
            new DotDeck.Core.AiCli.ContextBudget.SessionLogReader(Path.Combine(home, ".claude"), Path.Combine(home, ".codex")),
            new DotDeck.Core.AiCli.ContextBudget.McpMeasurementStore(PhysicalFileStore.Instance, Path.Combine(_temp, "local", "context-mcp.json")),
            new DotDeck.Core.AiCli.ContextBudget.McpProbe(host),
            () => new DotDeck.Core.AiCli.ContextBudget.ContextBudgetSettings { McpDirectMeasure = directMeasure, InstructionFileTokens = 100 },
            _ => Task.CompletedTask,
            (_, message) =>
            {
                confirms.Add(message);
                return false;
            }), _time));
        return (pane, host, confirms);
    }

    [Fact]
    public async Task Selecting_a_session_shows_its_context_and_mcp_measuring_asks_first()
    {
        var root = Path.Combine(_temp, "proj");
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), new string('x', 2000));
        File.WriteAllText(Path.Combine(root, ".mcp.json"), """{ "mcpServers": { "docs": { "command": "docs-server" } } }""");
        var (pane, host, confirms) = ContextPane(directMeasure: true);

        Start();
        pane.SelectedSession = pane.Sessions[0];
        await Eventually(() => pane.Context!.Report is not null && !pane.Context.IsBusy);

        var context = pane.Context!;
        Assert.True(context.HasTarget);
        Assert.Contains(context.Rows, r => r.Name == "proj/CLAUDE.md" && r.TokensText == "500");
        Assert.Contains(context.Suggestions, s => s.Text.Contains("100 이하 권장", StringComparison.Ordinal));
        Assert.Contains("파일 추정", context.SummaryText, StringComparison.Ordinal);
        Assert.StartsWith("사용자 설정분 약 ", context.SummaryText, StringComparison.Ordinal);
        Assert.Contains("전체 기본 소모는 첫 응답 후 측정", context.SummaryText, StringComparison.Ordinal);
        Assert.Empty(context.BreakdownText);
        var docs = Assert.Single(context.Rows, r => r.Name == "docs MCP");
        Assert.True(docs.CanMeasure);

        await context.MeasureAllCommand.ExecuteAsync(null);

        Assert.Equal(ContextBudgetViewModel.ConfirmMessage + "\n\n· docs (프로젝트 (.mcp.json))", Assert.Single(confirms));
        Assert.Equal(0, host.Starts); // declined: nothing started
    }

    [Fact]
    public async Task Mcp_measuring_is_hidden_unless_turned_on()
    {
        File.WriteAllText(Path.Combine(_temp, "proj", ".mcp.json"), """{ "mcpServers": { "docs": { "command": "docs-server" } } }""");
        var (pane, _, confirms) = ContextPane(directMeasure: false);

        Start();
        pane.SelectedSession = pane.Sessions[0];
        await Eventually(() => pane.Context!.Report is not null && !pane.Context.IsBusy);

        Assert.False(pane.Context!.CanMeasureMcp);
        var docs = Assert.Single(pane.Context.Rows, r => r.Name == "docs MCP");
        Assert.False(docs.CanMeasure);
        Assert.Contains("기타에 포함", docs.Detail, StringComparison.Ordinal);
        await pane.Context.MeasureAllCommand.ExecuteAsync(null);
        Assert.Empty(confirms);
    }

    [Fact]
    public async Task Disabled_mcp_servers_share_a_row_with_no_open_or_measure_action()
    {
        var home = Path.Combine(_temp, "home");
        Directory.CreateDirectory(home);
        var root = Path.Combine(_temp, "proj").Replace('\\', '/');
        File.WriteAllText(Path.Combine(home, ".claude.json"), $$"""
            {
              "mcpServers": { "first": { "command": "node" }, "second": { "command": "node" } },
              "projects": { "{{root}}": { "disabledMcpServers": ["first", "second"] } }
            }
            """);
        var (pane, _, confirms) = ContextPane(directMeasure: true);

        Start();
        pane.SelectedSession = pane.Sessions[0];
        await Eventually(() => pane.Context!.Report is not null && !pane.Context.IsBusy);

        var row = Assert.Single(pane.Context!.Rows, r => r.Item.Category == ContextCategory.Mcp);
        Assert.Equal("꺼진 MCP 2개", row.Name);
        Assert.Null(row.Path);
        Assert.False(row.HasPath);
        Assert.False(row.CanMeasure);
        await pane.Context.MeasureAllCommand.ExecuteAsync(null);
        Assert.Empty(confirms);
    }

    [Fact]
    public void User_tokens_exclude_builtin_skills_and_measured_mcp()
    {
        var report = new ContextReport(CliKind.Claude, null,
            [
                new(ContextCategory.Instructions, "Instructions", null, 100),
                new(ContextCategory.Skills, "Skill", null, 200),
                new(ContextCategory.Agents, "Agent", null, 300),
                new(ContextCategory.Skills, "Built-in skill", null, 400, BuiltIn: true),
                new(ContextCategory.Mcp, "Measured MCP", null, 500),
                new(ContextCategory.Mcp, "Disabled MCP", null, 0, ContextItemState.Disabled),
            ], [], [], _time.GetUtcNow());

        Assert.Equal(1_500, report.Total);
        Assert.Equal(600, ContextBudgetViewModel.UserTokens(report));
    }

    [Fact]
    public async Task A_status_line_only_row_retries_and_the_context_tab_uses_the_new_transcript()
    {
        var home = Path.Combine(_temp, "home");
        var (pane, _, _) = ContextPane(directMeasure: false);
        var logs = new SessionLogReader(Path.Combine(home, ".claude"), Path.Combine(home, ".codex"));
        var statusLines = new StatusLineContextStore(Path.Combine(home, ".dotdeck"));
        using var tracker = new ContextUsageTracker(new ContextUsageReader(logs, statusLines), () => new ContextBudgetSettings(),
            statusLines.Directory, _time);
        pane.AttachContextUsage(tracker);

        var session = Start();
        var row = Assert.Single(pane.Sessions);
        pane.SelectedSession = row;
        await Eventually(() => row.ContextUsage?.State == SessionLogState.WaitingFirstAnswer);
        var folder = Path.Combine(home, ".claude", "projects", SessionLogReader.ClaudeProjectFolder(session.Scope.Primary.Root));
        var logPath = Path.Combine(folder, session.ConversationId + ".jsonl");
        Directory.CreateDirectory(statusLines.Directory);
        File.WriteAllText(Path.Combine(statusLines.Directory, $"claude-{session.ConversationId}.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                session_id = session.ConversationId,
                transcript_path = logPath,
                measured_at = _time.GetUtcNow().AddMinutes(-6).ToUnixTimeMilliseconds(),
                context_window_size = 200_000,
                used_percentage = 10,
            }));
        _time.Advance(ContextUsageTracker.RetryInterval);
        pane.Tick();
        await Eventually(() => row.ContextUsage?.Source == ContextUsageSource.ClaudeStatusLine);
        Assert.Null(row.ContextLogPath);

        Directory.CreateDirectory(folder);
        File.WriteAllLines(logPath,
        [
            """{"type":"user","message":{"content":"hi"}}""",
            """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":40000}}}""",
        ]);
        _time.Advance(ContextUsageTracker.RetryInterval);
        pane.Tick();
        await Eventually(() => row.ContextUsage?.Source == ContextUsageSource.ClaudeLog
            && pane.Context?.Report?.Log?.LogPath == logPath);
        Assert.Equal("컨텍스트 20% (추정)", row.ContextText);
        Assert.Equal(logPath, row.ContextLogPath);
    }

    [Fact]
    public async Task Same_folder_terminal_rows_wait_when_the_only_transcript_could_belong_to_either()
    {
        var home = Path.Combine(_temp, "home");
        var root = Path.Combine(_temp, "proj");
        var process = new TerminalProcess { Ids = [1, 2, 3] };
        using var connection = new TerminalConnection(process, TerminalSize.Default, root);
        var terminal = new TerminalPaneViewModel("t1", "PS", connection, _ => { }, ui: null);
        var pane = Pane();
        var started = _time.GetUtcNow().AddMinutes(-1);
        pane.AttachTerminals(() => [terminal], id => id == 1 ? null : ("claude", started.AddSeconds(id)),
            _ => { }, parentOf: id => id == 1 ? null : 1);
        Assert.Equal(2, pane.Sessions.Count);

        var folder = Path.Combine(home, ".claude", "projects", SessionLogReader.ClaudeProjectFolder(root));
        Directory.CreateDirectory(folder);
        var timestamp = started.AddSeconds(4);
        File.WriteAllLines(Path.Combine(folder, Guid.NewGuid() + ".jsonl"),
        [
            $$$"""{"type":"user","timestamp":"{{{timestamp:O}}}","message":{"content":"hi"}}""",
            """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":40000}}}""",
        ]);
        var logs = new SessionLogReader(Path.Combine(home, ".claude"), Path.Combine(home, ".codex"));
        var statusLines = new StatusLineContextStore(Path.Combine(home, ".dotdeck"));
        using var tracker = new ContextUsageTracker(new ContextUsageReader(logs, statusLines), () => new ContextBudgetSettings(),
            statusLines.Directory, _time);
        pane.AttachContextUsage(tracker);

        await Eventually(() => pane.Sessions.All(row => row.ContextUsage is not null));
        Assert.All(pane.Sessions, row =>
        {
            Assert.Equal(SessionLogState.NotMatched, row.ContextUsage!.State);
            Assert.Null(row.ContextLogPath);
        });
    }

    [Fact]
    public async Task Every_row_shows_its_context_usage_updated_from_the_watched_log_and_kept_after_the_end()
    {
        var home = Path.Combine(_temp, "home");
        var pane = Pane();
        var logs = new DotDeck.Core.AiCli.ContextBudget.SessionLogReader(Path.Combine(home, ".claude"), Path.Combine(home, ".codex"));
        var statusLines = new DotDeck.Core.AiCli.ContextBudget.StatusLineContextStore(Path.Combine(home, ".dotdeck"));
        using var tracker = new ContextUsageTracker(new DotDeck.Core.AiCli.ContextBudget.ContextUsageReader(logs, statusLines),
            () => new DotDeck.Core.AiCli.ContextBudget.ContextBudgetSettings(), statusLines.Directory, _time);
        pane.AttachContextUsage(tracker);

        var session = Start();
        var item = Assert.Single(pane.Sessions);
        await Eventually(() => item.ContextText == "컨텍스트 첫 응답 후 측정"); // session id is known before its transcript is written

        static string Answer(string id, int read) =>
            "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":" + read.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}\n";
        var folder = Path.Combine(home, ".claude", "projects",
            DotDeck.Core.AiCli.ContextBudget.SessionLogReader.ClaudeProjectFolder(session.Scope.Primary.Root));
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, session.ConversationId + ".jsonl");
        File.WriteAllText(log, $$$"""{"type":"user","timestamp":"{{{session.StartedAt.UtcDateTime:O}}}","message":{"content":"hi"}}""" + "\n" + Answer("m1", 40_000));

        // Not matched yet: retried by the monitor's tick.
        _time.Advance(ContextUsageTracker.RetryInterval);
        pane.Tick();
        await Eventually(() => item.ContextText == "컨텍스트 20% (추정)");
        Assert.StartsWith("Claude: claude-opus-5-5 기준 추정(창 200k)", item.ContextToolTip, StringComparison.Ordinal);
        Assert.Equal(DotDeck.Core.AiCli.ContextBudget.ContextUsageLevel.Normal, item.ContextLevel);

        // Matched: the file is watched and appended answers show up without a tick.
        File.AppendAllText(log, Answer("m2", 150_000));
        await Eventually(() => item.ContextText == "컨텍스트 75% (추정)");
        Assert.Equal(DotDeck.Core.AiCli.ContextBudget.ContextUsageLevel.Warning, item.ContextLevel);

        _manager.Stop(session);
        await Eventually(() =>
        {
            pane.Refresh();
            return !item.IsRunning;
        });
        await Task.Delay(500, TestContext.Current.CancellationToken); // the read made at the end
        Assert.Equal("컨텍스트 75% (추정)", item.ContextText);
        File.AppendAllText(log, Answer("m3", 190_000)); // no longer watched: the last value stays
        await Task.Delay(ContextUsageTracker.Debounce + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        _time.Advance(ContextUsageTracker.RetryInterval);
        pane.Tick();
        Assert.Equal("컨텍스트 75% (추정)", item.ContextText);
    }

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
