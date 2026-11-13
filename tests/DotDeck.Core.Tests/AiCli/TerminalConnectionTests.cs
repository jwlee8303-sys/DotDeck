using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Files;
using DotDeck.Core.Paths;
using DotDeck.Core.Tests.Support;

namespace DotDeck.Core.Tests.AiCli;

public sealed class TerminalConnectionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DotDeck-term-test-" + Guid.NewGuid().ToString("N"));

    public TerminalConnectionTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "condition not reached");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Split_utf8_sequences_are_decoded_whole_and_replayed_to_late_viewers()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, new TerminalSize(80, 24));
        var bytes = Encoding.UTF8.GetBytes("한글 출력\r\n");

        process.Print(bytes[..2]); // inside the first syllable
        await Task.Delay(50, TestContext.Current.CancellationToken);
        process.Print(bytes[2..]);
        await Eventually(() => connection.Attach(_ => { }).Length > 0 && connection.Attach(_ => { }).EndsWith('\n'));

        var live = new StringBuilder();
        var replay = connection.Attach(t => { lock (live) live.Append(t); });
        process.Print(Encoding.UTF8.GetBytes("다음"));
        await Eventually(() => { lock (live) return live.ToString() == "다음"; });

        Assert.Equal("한글 출력\r\n", replay);
    }

    [Fact]
    public async Task Output_end_completes_and_detached_viewers_get_nothing()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var seen = 0;
        Action<string> viewer = _ => Interlocked.Increment(ref seen);
        connection.Attach(viewer);
        connection.Detach(viewer);

        process.Print("x"u8.ToArray());
        process.EndOutput();

        await connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, seen);
        Assert.Equal("x", connection.Attach(_ => { }));
    }

    [Fact]
    public async Task Replay_keeps_only_the_recent_part_cut_at_a_line_start()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        var line = new string('a', 999) + "\n";
        for (var i = 0; i < 1200; i++)
        {
            process.Print(Encoding.ASCII.GetBytes(line));
        }

        process.EndOutput();
        await connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var replay = connection.Attach(_ => { });
        Assert.True(replay.Length <= TerminalConnection.ReplayChars);
        Assert.True(replay.Length > TerminalConnection.ReplayChars - 2000);
        Assert.Equal(0, replay.Length % 1000);
    }

    [Fact]
    public async Task Input_is_written_in_order_as_utf8_and_binary_as_bytes()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default);

        connection.Send("ls 한글\r");
        connection.SendBinary("\u001b[M\u00ff");
        connection.Send(string.Empty);

        var expected = Encoding.UTF8.GetBytes("ls 한글\r").Concat(new byte[] { 0x1b, (byte)'[', (byte)'M', 0xff }).ToArray();
        await Eventually(() => process.Typed.Length >= expected.Length);
        Assert.Equal(expected, process.Typed);
    }

    [Fact]
    public void Resize_forwards_only_changes()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, new TerminalSize(80, 24));

        connection.Resize(new TerminalSize(80, 24));
        connection.Resize(new TerminalSize(100, 30));
        connection.Resize(new TerminalSize(100, 30));

        Assert.Equal([new TerminalSize(100, 30)], process.Sizes);
        Assert.Equal(new TerminalSize(100, 30), connection.Size);
    }

    [Fact]
    public void Dispose_disposes_the_process_once_and_input_after_it_is_dropped()
    {
        var process = new FakeTerminal();
        var connection = new TerminalConnection(process, TerminalSize.Default);

        connection.Dispose();
        connection.Dispose();
        connection.Send("late");

        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Input_after_the_console_closed_is_dropped_without_throwing()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        process.CloseInput();

        connection.Send("a");
        connection.Send("b");
        await Task.Delay(100, TestContext.Current.CancellationToken);
    }

    // ---- ShellTerminal ----

    [Theory]
    [InlineData("\a")]
    [InlineData("\u001b\\")]
    public async Task Working_directory_tracks_split_osc7_without_changing_replay(string terminator)
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default, _temp);
        var folder = Path.Combine(_temp, "한글 ' # 폴더");
        var sequence = "\u001b]7;" + new Uri(folder).AbsoluteUri + terminator;
        var bytes = Encoding.UTF8.GetBytes(sequence);
        foreach (var b in bytes) process.Print([b]);
        process.EndOutput();
        await connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(folder, connection.WorkingDirectory);
        Assert.Equal(sequence, connection.Attach(_ => { }));
    }

    [Fact]
    public async Task Malformed_osc_and_non_file_locations_keep_the_launch_directory()
    {
        using var process = new FakeTerminal();
        using var connection = new TerminalConnection(process, TerminalSize.Default, _temp);
        process.Print(Encoding.UTF8.GetBytes("\u001b]7;https://example.test/path\a\u001b]7;not a uri\a\u001b]0;window title\a"));
        process.EndOutput();
        await connection.OutputEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(_temp, connection.WorkingDirectory);
    }

    private string Exe(params string[] parts)
    {
        var path = Path.Combine([_temp, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Pwsh_on_path_wins_then_program_files_then_windows_powershell()
    {
        var env = new FakeProcessEnvironment();
        env.Folders[Environment.SpecialFolder.ProgramFiles] = Path.Combine(_temp, "pf");
        env.Folders[Environment.SpecialFolder.System] = Path.Combine(_temp, "sys");
        env.Variables["PATH"] = $"relative;\"{Path.Combine(_temp, "bin")}\"";
        var legacy = Exe("sys", "WindowsPowerShell", "v1.0", "powershell.exe");

        Assert.Equal(new ShellLocation(legacy, true), ShellTerminal.Locate(env, PhysicalFileStore.Instance));

        var installed = Exe("pf", "PowerShell", "7", "pwsh.exe");
        Assert.Equal(new ShellLocation(installed, false), ShellTerminal.Locate(env, PhysicalFileStore.Instance));

        var onPath = Exe("bin", "pwsh.exe");
        Assert.Equal(new ShellLocation(onPath, false), ShellTerminal.Locate(env, PhysicalFileStore.Instance));
    }

    [Fact]
    public void Relative_path_entries_are_ignored_and_nothing_found_is_null()
    {
        var env = new FakeProcessEnvironment { CurrentDirectory = _temp };
        env.Variables["PATH"] = ".;bin";
        Exe("bin", "pwsh.exe");
        Exe("pwsh.exe");

        Assert.Null(ShellTerminal.Locate(env, PhysicalFileStore.Instance));
    }

    [Fact]
    public void Plan_initializes_utf8_and_passes_sandbox_homes_only_with_home_override()
    {
        var shell = new ShellLocation(@"C:\tools\pwsh.exe", false);
        var sandbox = new AppPaths(@"D:\sandbox", HomeSource.CommandLine, @"D:\r", @"D:\l");
        var real = new AppPaths(@"C:\Users\tester", HomeSource.UserProfile, @"D:\r", @"D:\l");

        var plan = ShellTerminal.Plan(shell, @"D:\proj", sandbox);

        Assert.Equal(["-NoLogo", "-NoExit", "-Command", ShellTerminal.Utf8Initialization + "; " + ShellTerminal.PromptInitialization], plan.Arguments);
        Assert.Equal(@"D:\proj", plan.WorkingDirectory);
        Assert.Null(plan.StandardInput);
        Assert.Equal(@"D:\sandbox\.claude", plan.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(@"D:\sandbox\.codex", plan.Environment["CODEX_HOME"]);
        Assert.Empty(ShellTerminal.Plan(shell, @"D:\proj", real).Environment);
        ForbiddenOptions.Validate(plan);
    }

    private sealed class FakeTerminal : ITerminalProcess
    {
        private readonly AnonymousPipeServerStream _output = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _input = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _inputClient;
        private readonly MemoryStream _typed = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTerminal()
        {
            Output = new AnonymousPipeClientStream(PipeDirection.In, _output.ClientSafePipeHandle);
            _inputClient = new AnonymousPipeClientStream(PipeDirection.Out, _input.ClientSafePipeHandle);
            Input = _inputClient;
            _ = Task.Run(() =>
            {
                var buffer = new byte[256];
                try
                {
                    int read;
                    while ((read = _input.Read(buffer)) > 0)
                    {
                        lock (_typed)
                        {
                            _typed.Write(buffer, 0, read);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            });
        }

        public List<TerminalSize> Sizes { get; } = [];

        public int DisposeCount { get; private set; }

        public byte[] Typed
        {
            get
            {
                lock (_typed)
                {
                    return _typed.ToArray();
                }
            }
        }

        public int ProcessId => 1234;

        public Stream Output { get; }

        public Stream Input { get; }

        public Task<int> Exited => _exit.Task;

        public void Print(byte[] bytes) => _output.Write(bytes);

        public void EndOutput() => _output.Dispose();

        public void CloseInput() => _input.Dispose();

        public void Resize(TerminalSize size) => Sizes.Add(size);

        public IReadOnlyList<int> JobProcessIds() => [];

        public void Terminate(int exitCode = 1) => _exit.TrySetResult(exitCode);

        public void Dispose()
        {
            DisposeCount++;
            _output.Dispose();
            Output.Dispose();
            _inputClient.Dispose();
            _input.Dispose();
            _exit.TrySetResult(-1);
        }
    }
}
