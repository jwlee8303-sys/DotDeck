using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Presets;
using DotDeck.Core.AiCli.Scope;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Projects;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class AiSessionManagerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTerminalLauncher _terminals = new();
    private readonly List<ProjectDefinition> _projects;
    private readonly string _main;
    private readonly string _web;

    public AiSessionManagerTests()
    {
        _main = _temp.Combine("DotDeck");
        _web = _temp.Combine("웹 게임");
        Directory.CreateDirectory(_main);
        Directory.CreateDirectory(_web);
        _projects =
        [
            new() { Id = "a", Kind = ProjectKind.Folder, Name = "DotDeck", Root = _main },
            new() { Id = "b", Kind = ProjectKind.Folder, Name = "웹게임", Root = _web },
        ];
    }

    public void Dispose() => _temp.Dispose();

    private AppPaths Paths(HomeSource source = HomeSource.CommandLine) =>
        new(_temp.Combine("home"), source, _temp.Combine("roaming"), _temp.Combine("local"));

    private AiSessionManager Manager(AppPaths? paths = null, SessionPresetStore? presets = null, TimeSpan? heartbeat = null, bool terminals = false) => new(new AiSessionEnvironment
    {
        Paths = paths ?? Paths(),
        Store = PhysicalFileStore.Instance,
        Projects = () => _projects,
        Launcher = _launcher,
        TerminalLauncher = terminals ? _terminals : null,
        Locate = cli => new CliLocatorResult(new CliLocation(cli, @"C:\tools\claude.exe", []), null),
        GuardExecutable = @"C:\Program Files\DotDeck\DotDeck.exe",
        Presets = presets,
        HeartbeatTimeout = heartbeat ?? TimeSpan.FromMinutes(5),
    });

    private static AiSessionStartRequest Request(params string[] additional) => new()
    {
        PrimaryProjectId = "a",
        AdditionalProjectIds = additional,
        Prompt = "README 정리해줘",
    };

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
    public void Preview_shows_the_command_line_without_writing_or_launching()
    {
        using var manager = Manager();

        var preview = manager.PreviewCommandLine(Request("b"));

        Assert.StartsWith("\"C:\\tools\\claude.exe\"", preview, StringComparison.OrdinalIgnoreCase);
        // The prompt goes through standard input, never onto the command line.
        Assert.DoesNotContain("README 정리해줘", preview, StringComparison.Ordinal);
        Assert.Contains("표준 입력", preview, StringComparison.Ordinal);
        Assert.Contains("(새 세션)", preview, StringComparison.Ordinal);
        Assert.Empty(_launcher.Plans);
        Assert.False(Directory.Exists(Paths().AiSessionsDir) && Directory.EnumerateFileSystemEntries(Paths().AiSessionsDir).Any());
    }

    [Fact]
    public void Preview_returns_the_reason_when_the_request_cannot_start()
    {
        using var manager = Manager();

        var preview = manager.PreviewCommandLine(Request() with { PrimaryProjectId = "unknown" });

        Assert.DoesNotContain("claude.exe", preview, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(preview);
        Assert.Empty(_launcher.Plans);
    }

    [Fact]
    public async Task Start_prepares_the_session_folder_and_launches_the_plan()
    {
        using var manager = Manager();

        var session = manager.Start(Request("b"));

        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(_main, plan.WorkingDirectory);
        Assert.Equal("README 정리해줘", plan.StandardInput);
        Assert.Equal(Path.Combine(session.Directory, "settings.json"), plan.Arguments[plan.Arguments.ToList().IndexOf("--settings") + 1]);
        Assert.Equal(session.ConversationId, plan.Arguments[plan.Arguments.ToList().IndexOf("--session-id") + 1]);
        Assert.StartsWith(_temp.Combine("local", "sessions"), session.Directory, StringComparison.OrdinalIgnoreCase);

        var scope = ScopeFile.Read(Path.Combine(session.Directory, "scope.json"));
        Assert.Equal(session.Id, scope.SessionId);
        Assert.Equal([_main, _web], scope.Roots.Select(r => r.Path));
        Assert.Contains(Path.Combine(Paths().ClaudeHome, "skills"), scope.ReadOnlyRoots);
        Assert.True(File.Exists(Path.Combine(session.Directory, "settings.json")));
        Assert.Contains("웹게임", File.ReadAllText(Path.Combine(session.Directory, "context.md")), StringComparison.Ordinal);
        Assert.Equal("Claude · DotDeck (+웹게임)", session.Title);
        Assert.Equal(1, manager.RunningCount);

        var lines = new List<string>();
        session.OutputLine += (_, line, isError) => { lock (lines) { lines.Add((isError ? "E:" : "O:") + line); } };
        _launcher.Last.WriteStdout("{\"type\":\"result\"}\n");
        _launcher.Last.WriteStderr("경고\n");
        File.WriteAllText(Path.Combine(session.Directory, "heartbeat"), "x");
        _launcher.Last.Exit(0);

        await Eventually(() => session.State == AiSessionState.Exited);
        await Eventually(() => { lock (lines) { return lines.Count == 2; } });
        Assert.Contains("O:{\"type\":\"result\"}", lines);
        Assert.Contains("E:경고", lines);
        Assert.Equal(0, manager.RunningCount);
        await Eventually(() => _launcher.Last.Disposed);
        Assert.True(_launcher.Last.Terminated, "children left in the job are ended with the main process");
    }

    [Fact]
    public async Task Exit_without_heartbeat_is_a_failure_even_with_code_zero()
    {
        using var manager = Manager();
        var session = manager.Start(Request());

        _launcher.Last.Exit(0);

        await Eventually(() => session.State == AiSessionState.Failed);
        Assert.Contains("가드 훅", session.Message, StringComparison.Ordinal);
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public void Start_snapshots_the_scope_and_the_summary_reports_changes()
    {
        var file = Path.Combine(_web, "notes.md");
        File.WriteAllText(file, "처음");
        using var manager = Manager();

        var session = manager.Start(Request("b"));

        Assert.True(session.Snapshot!.FullCapture);
        Assert.Equal(1, session.Snapshot.CapturedFiles);
        File.WriteAllText(file, "AI가 바꾼 내용");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(_main, "new.md"), "새 파일");

        var summary = manager.BuildChangeSummary(session)!;

        Assert.Equal(
            [("new.md", DotDeck.Core.AiCli.Snapshots.ChangeKind.Created), ("notes.md", DotDeck.Core.AiCli.Snapshots.ChangeKind.Modified)],
            summary.Items.Select(i => (Path.GetFileName(i.Path), i.Kind)).OrderBy(x => x.Item1, StringComparer.Ordinal));
        Assert.NotNull(summary.Items.Single(i => i.Path == file).PreviousFile);
    }

    [Fact]
    public void Sandbox_homes_are_passed_only_in_sandbox_mode()
    {
        using (var sandbox = Manager(Paths(HomeSource.CommandLine)))
        {
            sandbox.Start(Request());
        }

        Assert.Equal(Paths().ClaudeHome, _launcher.Plans[0].Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(Paths().CodexHome, _launcher.Plans[0].Environment["CODEX_HOME"]);

        using (var real = Manager(Paths(HomeSource.UserProfile)))
        {
            real.Start(Request());
        }

        Assert.False(_launcher.Plans[1].Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.False(_launcher.Plans[1].Environment.ContainsKey("CODEX_HOME"));
    }

    [Fact]
    public async Task Failing_exit_code_marks_the_session_failed()
    {
        using var manager = Manager();
        var session = manager.Start(Request());

        _launcher.Last.Exit(3);

        await Eventually(() => session.State == AiSessionState.Failed);
        Assert.Equal(3, session.ExitCode);
    }

    [Fact]
    public async Task Missing_heartbeat_stops_the_session()
    {
        using var manager = Manager(heartbeat: TimeSpan.FromMilliseconds(100));
        var session = manager.Start(Request());

        await Eventually(() => session.State == AiSessionState.Failed);
        Assert.True(_launcher.Last.Terminated);
        Assert.Contains("가드 훅", session.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heartbeat_keeps_the_session_running()
    {
        using var manager = Manager(heartbeat: TimeSpan.FromMilliseconds(300));
        var session = manager.Start(Request());
        File.WriteAllText(Path.Combine(session.Directory, "heartbeat"), "x");

        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.Equal(AiSessionState.Running, session.State);
        Assert.False(_launcher.Last.Terminated);
        Assert.True(session.GuardConnected);
    }

    [Fact]
    public async Task Guard_connection_is_reported_as_soon_as_the_heartbeat_appears()
    {
        using var manager = Manager();
        var session = manager.Start(Request());
        var changes = 0;
        session.StateChanged += _ => Interlocked.Increment(ref changes);
        Assert.False(session.GuardConnected);

        File.WriteAllText(Path.Combine(session.Directory, "heartbeat"), "x");

        await Eventually(() => session.GuardConnected);
        await Eventually(() => Volatile.Read(ref changes) == 1);
        Assert.Equal(AiSessionState.Running, session.State);
    }

    [Fact]
    public async Task Late_viewers_get_buffered_output_once_and_then_live_lines()
    {
        using var manager = Manager();
        var session = manager.Start(Request());
        _launcher.Last.WriteStdout("one\ntwo\n");
        await Eventually(() => session.AttachOutput((_, _, _) => { }).Count == 2);

        var live = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var buffered = session.AttachOutput((_, line, _) => live.Enqueue(line));
        _launcher.Last.WriteStderr("three\n");
        await Eventually(() => live.Count == 1);

        Assert.Equal(["one", "two"], buffered.Select(l => l.Line));
        Assert.Equal(["three"], live);

        session.DetachOutput((_, _, _) => { }); // unknown viewers are ignored
    }

    [Fact]
    public async Task Output_buffer_is_bounded_by_size()
    {
        using var manager = Manager();
        var session = manager.Start(Request());
        var big = new string('x', 1024 * 1024);
        for (var i = 0; i < 6; i++)
        {
            _launcher.Last.WriteStdout(big + "\n");
        }

        _launcher.Last.WriteStdout("last\n");
        await Eventually(() => session.AttachOutput((_, _, _) => { }) is { Count: > 0 } lines && lines[^1].Line == "last");

        var kept = session.AttachOutput((_, _, _) => { });
        Assert.True(kept.Sum(l => (long)l.Line.Length) <= AiSession.OutputBufferChars);
        Assert.True(kept.Count < 7);
    }

    [Fact]
    public async Task A_single_huge_line_reaches_live_viewers_but_is_not_buffered()
    {
        using var manager = Manager();
        var session = manager.Start(Request());
        var live = 0L;
        session.AttachOutput((_, line, _) => Interlocked.Add(ref live, line.Length));

        _launcher.Last.WriteStdout(new string('y', (int)AiSession.OutputBufferChars + 10) + "\nafter\n");
        await Eventually(() => Interlocked.Read(ref live) >= AiSession.OutputBufferChars + 15);

        var kept = session.AttachOutput((_, _, _) => { });
        Assert.Equal(["after"], kept.Select(l => l.Line));
    }

    [Fact]
    public void Revert_planner_without_a_recycle_bin_refuses_created_files()
    {
        using var manager = Manager();
        var session = manager.Start(Request());
        var created = Path.Combine(_main, "new.md");
        File.WriteAllText(created, "x");
        var item = manager.BuildChangeSummary(session)!.Items.Single(i => i.Path == created);

        var planner = manager.CreateRevertPlanner(session, new DotDeck.Core.Documents.SafeWriter(PhysicalFileStore.Instance,
            new DotDeck.Core.Documents.BackupStore(PhysicalFileStore.Instance, _temp.Combine("user-backups"), TimeProvider.System)));

        Assert.Equal(DotDeck.Core.AiCli.Snapshots.RevertStatus.NotRevertible, planner.Revert(item).Status);
        Assert.True(File.Exists(created));
    }

    [Fact]
    public async Task Stop_terminates_the_tree_once()
    {
        using var manager = Manager();
        var session = manager.Start(Request());

        manager.Stop(session);
        await Eventually(() => _launcher.Last.Disposed); // exit cleanup has run
        var afterFirst = _launcher.Last.TerminateCount;
        manager.Stop(session, "두 번째");

        Assert.Equal(AiSessionState.Stopped, session.State);
        Assert.Equal("사용자가 중지했습니다.", session.Message);
        Assert.True(afterFirst >= 1);
        Assert.Equal(afterFirst, _launcher.Last.TerminateCount);
    }

    [Fact]
    public void Removing_an_additional_project_narrows_scope_json_at_once()
    {
        using var manager = Manager();
        var session = manager.Start(Request("b"));

        _projects.RemoveAt(1);
        manager.OnProjectsChanged();

        var scope = ScopeFile.Read(Path.Combine(session.Directory, "scope.json"));
        Assert.Equal(2, scope.Generation);
        Assert.Equal([_main], scope.Roots.Select(r => r.Path));
        Assert.Equal("Claude · DotDeck", session.Title);
        Assert.Equal(AiSessionState.Running, session.State);
    }

    [Fact]
    public void Project_removed_while_the_session_starts_is_dropped_once_it_is_registered()
    {
        using var manager = Manager();
        _launcher.OnStart = () =>
        {
            _projects.RemoveAt(1);
            manager.OnProjectsChanged(); // the session is not registered yet: this call cannot see it
        };

        var session = manager.Start(Request("b"));

        var scope = ScopeFile.Read(Path.Combine(session.Directory, "scope.json"));
        Assert.Equal([_main], scope.Roots.Select(r => r.Path));
        Assert.Equal(AiSessionState.Running, session.State);
    }

    [Fact]
    public void Moving_an_additional_project_removes_it_rather_than_widening_the_scope()
    {
        using var manager = Manager();
        var session = manager.Start(Request("b"));
        var moved = _temp.Combine("moved");
        Directory.CreateDirectory(moved);

        _projects[1] = _projects[1] with { Root = moved };
        manager.OnProjectsChanged();

        Assert.Equal([_main], ScopeFile.Read(Path.Combine(session.Directory, "scope.json")).Roots.Select(r => r.Path));
    }

    [Fact]
    public void Losing_the_primary_project_stops_the_session()
    {
        using var manager = Manager();
        var session = manager.Start(Request("b"));

        _projects.RemoveAt(0);
        manager.OnProjectsChanged();

        Assert.Equal(AiSessionState.Stopped, session.State);
        Assert.True(_launcher.Last.Terminated);
    }

    [Fact]
    public void Resume_uses_the_remembered_conversation_of_the_scope()
    {
        var presets = new SessionPresetStore(PhysicalFileStore.Instance, Paths().AiSessionsFile);
        presets.Load();
        using var manager = Manager(presets: presets);

        var first = manager.Start(Request("b"));
        manager.Stop(first); // one CLI per conversation
        var resumed = manager.Start(Request("b") with { ResumeConversation = true });
        var fresh = manager.Start(Request());

        Assert.Equal(first.ConversationId, resumed.ConversationId);
        Assert.Contains("--resume", _launcher.Plans[1].Arguments);
        Assert.NotEqual(first.ConversationId, fresh.ConversationId);
        Assert.Contains("--session-id", _launcher.Plans[2].Arguments);
    }

    [Theory]
    [InlineData(CliKind.Codex, LaunchMode.NonInteractive, "프롬프트", "5c")]
    [InlineData(CliKind.Claude, LaunchMode.Interactive, "프롬프트", "대화형")]
    [InlineData(CliKind.Claude, LaunchMode.NonInteractive, " ", "프롬프트")]
    public void Unsupported_or_incomplete_requests_are_refused(CliKind cli, LaunchMode mode, string prompt, string expected)
    {
        using var manager = Manager();

        var error = Assert.Throws<AiSessionStartException>(() => manager.Start(Request() with { Cli = cli, Mode = mode, Prompt = prompt }));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Plans);
    }

    [Fact]
    public void Missing_cli_and_unknown_project_are_reported()
    {
        using var noCli = new AiSessionManager(new AiSessionEnvironment
        {
            Paths = Paths(),
            Store = PhysicalFileStore.Instance,
            Projects = () => _projects,
            Launcher = _launcher,
            Locate = _ => new CliLocatorResult(null, "claude 실행 파일을 PATH에서 찾을 수 없습니다."),
            GuardExecutable = @"C:\d.exe",
        });

        Assert.Contains("PATH", Assert.Throws<AiSessionStartException>(() => noCli.Start(Request())).Message, StringComparison.Ordinal);
        Assert.Throws<AiSessionStartException>(() => noCli.Start(Request() with { PrimaryProjectId = "zzz" }));
    }

    [Fact]
    public void Launch_failure_is_reported_and_nothing_is_tracked()
    {
        _launcher.Fail = true;
        using var manager = Manager();

        Assert.Throws<AiSessionStartException>(() => manager.Start(Request()));
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public void Dispose_stops_running_sessions()
    {
        var manager = Manager();
        var session = manager.Start(Request());

        manager.Dispose();

        Assert.Equal(AiSessionState.Stopped, session.State);
        Assert.True(_launcher.Last.Disposed);
    }

    [Fact]
    public async Task Interactive_session_runs_in_a_terminal_without_a_prompt()
    {
        using var manager = Manager(terminals: true);

        var session = manager.Start(Request("b") with { Mode = LaunchMode.Interactive, Prompt = null });

        var (plan, size) = Assert.Single(_terminals.Starts);
        Assert.Empty(_launcher.Plans);
        Assert.Null(plan.StandardInput);
        Assert.DoesNotContain("-p", plan.Arguments);
        Assert.Contains("--settings", plan.Arguments);
        Assert.Equal("Claude · DotDeck (+웹게임)", plan.Arguments[plan.Arguments.ToList().IndexOf("-n") + 1]);
        Assert.Equal(TerminalSize.Default, size);
        Assert.NotNull(session.Terminal);
        Assert.Equal(LaunchMode.Interactive, session.Mode);

        _terminals.Last.Print("TUI 화면");
        File.WriteAllText(Path.Combine(session.Directory, "heartbeat"), "x");
        await Eventually(() => session.GuardConnected);
        await Eventually(() => session.Terminal!.Attach(_ => { }) == "TUI 화면");

        _terminals.Last.Exit(0);

        await Eventually(() => session.State == AiSessionState.Exited);
        Assert.True(_terminals.Last.Disposed);
        Assert.Equal("TUI 화면", session.Terminal!.Attach(_ => { })); // replay survives the exit
    }

    [Fact]
    public async Task Interactive_prompt_is_not_passed_and_stop_terminates_the_terminal()
    {
        using var manager = Manager(terminals: true);
        var session = manager.Start(Request() with { Mode = LaunchMode.Interactive });

        Assert.DoesNotContain(_terminals.Starts[0].Plan.Arguments, a => a.Contains("README 정리해줘", StringComparison.Ordinal));

        manager.Stop(session);

        Assert.Equal(AiSessionState.Stopped, session.State);
        await Eventually(() => _terminals.Last.Terminated);
    }

    [Fact]
    public async Task Interactive_session_without_heartbeat_fails_after_exit()
    {
        using var manager = Manager(terminals: true);
        var session = manager.Start(Request() with { Mode = LaunchMode.Interactive });

        _terminals.Last.Exit(0);

        await Eventually(() => session.State == AiSessionState.Failed);
        Assert.Contains("가드 훅", session.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restart_resumes_the_given_conversation_even_in_another_scope()
    {
        using var manager = Manager(terminals: true);
        var first = manager.Start(Request() with { Mode = LaunchMode.Interactive });
        manager.Stop(first); // one CLI per conversation

        var restarted = manager.Start(Request("b") with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId });
        var notResumed = manager.Start(Request("b") with { Mode = LaunchMode.Interactive, ResumeConversationId = first.ConversationId });
        var invalid = manager.Start(Request("b") with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = "not-a-uuid" });

        Assert.Equal(first.ConversationId, restarted.ConversationId);
        Assert.Contains("--resume", _terminals.Starts[1].Plan.Arguments);
        Assert.NotEqual(first.ConversationId, notResumed.ConversationId);
        Assert.NotEqual("not-a-uuid", invalid.ConversationId);
        Assert.DoesNotContain("not-a-uuid", _terminals.Starts[3].Plan.Arguments);
    }

    [Fact]
    public void A_running_conversation_cannot_be_resumed_twice()
    {
        using var manager = Manager(terminals: true);
        var first = manager.Start(Request() with { Mode = LaunchMode.Interactive });
        manager.Stop(first);
        var second = manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId });

        // Restarting again from the old (stopped) tab while the resumed one runs.
        var error = Assert.Throws<AiSessionStartException>(() =>
            manager.Start(Request("b") with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId }));

        Assert.Contains("다른 세션에서 실행 중", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, _terminals.Starts.Count);

        manager.Stop(second);
        var third = manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId });
        Assert.Equal(first.ConversationId, third.ConversationId);
    }

    [Fact]
    public async Task A_stopped_session_keeps_its_conversation_until_its_process_ends()
    {
        using var manager = Manager(terminals: true);
        _terminals.TerminateExits = false; // the process takes its time to end
        var first = manager.Start(Request() with { Mode = LaunchMode.Interactive });
        manager.Stop(first);

        Assert.Equal(AiSessionState.Stopped, first.State);
        Assert.Throws<AiSessionStartException>(() =>
            manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId }));

        _terminals.Terminals[0].Exit(1);
        await first.WhenProcessEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var resumed = manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = first.ConversationId });

        Assert.Equal(first.ConversationId, resumed.ConversationId);
    }

    [Fact]
    public void Conversation_ids_are_compared_in_one_spelling()
    {
        using var manager = Manager(terminals: true);
        var first = manager.Start(Request() with { Mode = LaunchMode.Interactive });
        manager.Stop(first);
        var upper = first.ConversationId!.ToUpperInvariant();
        var braced = "{" + first.ConversationId + "}";

        var resumed = manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = upper });

        Assert.Equal(first.ConversationId, resumed.ConversationId);
        Assert.Contains(first.ConversationId, _terminals.Starts[1].Plan.Arguments);
        Assert.Throws<AiSessionStartException>(() =>
            manager.Start(Request() with { Mode = LaunchMode.Interactive, ResumeConversation = true, ResumeConversationId = braced }));
    }

    [Fact]
    public void Excluding_an_additional_project_rewrites_the_scope_and_the_primary_stays()
    {
        using var manager = Manager();
        var session = manager.Start(Request("b"));
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        Assert.NotNull(manager.ExcludeProject(session, "a"));
        Assert.NotNull(manager.ExcludeProject(session, "zzz"));
        Assert.Null(manager.ExcludeProject(session, "b"));

        Assert.Empty(session.Scope.Additional);
        var scope = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Directory, "scope.json")))!;
        Assert.Equal(2, scope["generation"]!.GetValue<long>());
        Assert.Single(scope["roots"]!.AsArray());
        Assert.Equal(1, changed);

        manager.Stop(session);
        Assert.NotNull(manager.ExcludeProject(session, "b"));
    }

    private sealed class FakeTerminalLauncher : ITerminalLauncher
    {
        public List<(CliLaunchPlan Plan, TerminalSize Size)> Starts { get; } = [];

        public List<FakeTerminal> Terminals { get; } = [];

        public FakeTerminal Last => Terminals[^1];

        /// <summary>False: Terminate only records the call; the test ends the process with Exit.</summary>
        public bool TerminateExits { get; set; } = true;

        public ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size)
        {
            Starts.Add((plan, size));
            var terminal = new FakeTerminal { TerminateExits = TerminateExits };
            Terminals.Add(terminal);
            return terminal;
        }
    }

    private sealed class FakeTerminal : ITerminalProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.IO.Pipes.AnonymousPipeServerStream _output = new(System.IO.Pipes.PipeDirection.Out);

        public FakeTerminal()
        {
            Output = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _output.ClientSafePipeHandle);
        }

        public int ProcessId => 4321;

        public Stream Output { get; }

        public Stream Input { get; } = new MemoryStream();

        public Task<int> Exited => _exit.Task;

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public bool TerminateExits { get; init; } = true;

        public void Print(string text) => _output.Write(Encoding.UTF8.GetBytes(text));

        public void Exit(int code)
        {
            _output.Dispose();
            _exit.TrySetResult(code);
        }

        public void Resize(TerminalSize size)
        {
        }

        public IReadOnlyList<int> JobProcessIds() => [];

        public void Terminate(int exitCode = 1)
        {
            Terminated = true;
            if (TerminateExits)
            {
                Exit(exitCode);
            }
        }

        public void Dispose()
        {
            Disposed = true;
            Exit(-1);
            Output.Dispose();
        }
    }

    private sealed class FakeLauncher : ICliProcessLauncher
    {
        public List<CliLaunchPlan> Plans { get; } = [];

        public List<FakeProcess> Processes { get; } = [];

        public FakeProcess Last => Processes[^1];

        public bool Fail { get; set; }

        /// <summary>Runs inside Start (after the scope was made, before the session is registered).</summary>
        public Action? OnStart { get; set; }

        public ICliProcess Start(CliLaunchPlan plan)
        {
            if (Fail)
            {
                throw new IOException("시작 실패");
            }

            OnStart?.Invoke();
            Plans.Add(plan);
            var process = new FakeProcess();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : ICliProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.IO.Pipes.AnonymousPipeServerStream _stdoutServer = new(System.IO.Pipes.PipeDirection.Out);
        private readonly System.IO.Pipes.AnonymousPipeServerStream _stderrServer = new(System.IO.Pipes.PipeDirection.Out);

        public FakeProcess()
        {
            StandardOutput = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _stdoutServer.ClientSafePipeHandle);
            StandardError = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _stderrServer.ClientSafePipeHandle);
        }

        public int ProcessId => 1234;

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public Task<int> Exited => _exit.Task;

        public int TerminateCount { get; private set; }

        public bool Terminated => TerminateCount > 0;

        public bool Disposed { get; private set; }

        public void WriteStdout(string text) => _stdoutServer.Write(Encoding.UTF8.GetBytes(text));

        public void WriteStderr(string text) => _stderrServer.Write(Encoding.UTF8.GetBytes(text));

        public void Exit(int code)
        {
            _stdoutServer.Dispose();
            _stderrServer.Dispose();
            _exit.TrySetResult(code);
        }

        public IReadOnlyList<int> JobProcessIds() => Exited.IsCompleted ? [] : [ProcessId];

        public void Terminate(int exitCode = 1)
        {
            TerminateCount++;
            Exit(exitCode);
        }

        public void Dispose()
        {
            Disposed = true;
            Exit(1);
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
