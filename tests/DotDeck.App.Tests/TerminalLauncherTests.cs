using System.Diagnostics;
using System.IO;
using System.Text;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;
using DotDeck.Core.Paths;

namespace DotDeck.App.Tests;

/// <summary>Real processes in a real pseudo console: VT output, typed input, resizing, exit and tree termination.</summary>
public sealed class TerminalLauncherTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private readonly string _cwd = Path.Combine(Path.GetTempPath(), "dotdeck-pty " + Guid.NewGuid().ToString("N"));

    public TerminalLauncherTests() => Directory.CreateDirectory(_cwd);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cwd, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CliLaunchPlan Plan(string[] args, string? stdin = null) => new()
    {
        Executable = Cmd,
        Arguments = args,
        WorkingDirectory = _cwd,
        StandardInput = stdin,
    };

    private static ITerminalProcess Start(CliLaunchPlan plan, TerminalSize? size = null) =>
        new JobProcessLauncher().Start(plan, size ?? new TerminalSize(100, 30));

    /// <summary>Reads the whole output stream on a pool thread (it ends after exit, once the console is closed).</summary>
    private sealed class OutputCollector
    {
        private readonly MemoryStream _bytes = new();
        private readonly Task _reader;

        public OutputCollector(ITerminalProcess process)
        {
            _reader = Task.Run(() =>
            {
                var buffer = new byte[4096];
                try
                {
                    int read;
                    while ((read = process.Output.Read(buffer)) > 0)
                    {
                        lock (_bytes)
                        {
                            _bytes.Write(buffer, 0, read);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            });
        }

        public string Text
        {
            get
            {
                lock (_bytes)
                {
                    return Encoding.UTF8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length);
                }
            }
        }

        public Task Completion => _reader;

        public async Task WaitForAsync(string text)
        {
            var watch = Stopwatch.StartNew();
            while (!Text.Contains(text, StringComparison.Ordinal))
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"not seen: {text}\n---\n{Text}");
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }
        }
    }

    private static void Type(ITerminalProcess process, string text)
    {
        process.Input.Write(Encoding.UTF8.GetBytes(text));
        process.Input.Flush();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PowerShell_reports_the_last_folder_and_restores_an_idle_prompt(bool legacy)
    {
        var executable = legacy
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(executable)) Assert.Skip("This PowerShell edition is not installed.");
        var paths = new AppPaths(_cwd, HomeSource.CommandLine, Path.Combine(_cwd, "r"), Path.Combine(_cwd, "l"));
        var shell = new ShellLocation(executable, legacy);
        var target = Path.Combine(_cwd, "한글 ' # 폴더");
        Directory.CreateDirectory(target);
        using var process = Start(ShellTerminal.Plan(shell, _cwd, paths));
        using var connection = new TerminalConnection(process, TerminalSize.Default);
        await WaitForFolder(connection, _cwd);
        connection.Send("Set-Location -LiteralPath '" + target.Replace("'", "''", StringComparison.Ordinal) + "'\r");
        await WaitForFolder(connection, target);
        connection.Terminate();

        using var restartedProcess = Start(ShellTerminal.Plan(shell, connection.WorkingDirectory!, paths));
        using var restarted = new TerminalConnection(restartedProcess, TerminalSize.Default);
        await WaitForFolder(restarted, target);
        Assert.False(restarted.Exited.IsCompleted);
        restarted.Send("exit\r");
        Assert.Equal(0, await restarted.Exited.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
    }

    private static async Task WaitForFolder(TerminalConnection connection, string folder)
    {
        var watch = Stopwatch.StartNew();
        while (!string.Equals(folder, connection.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The PowerShell prompt did not report its folder.");
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Output_is_read_and_ends_after_exit()
    {
        using var process = Start(Plan(["/d", "/c", "echo dotdeck-pty-out & exit 3"]));
        var output = new OutputCollector(process);

        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Equal(3, exit);
        Assert.Contains("dotdeck-pty-out", output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_input_including_korean_reaches_the_process()
    {
        using var process = Start(Plan(["/d", "/q"]));
        var output = new OutputCollector(process);

        Type(process, "echo 입력-확인-한글\r");
        await output.WaitForAsync("입력-확인-한글");
        Type(process, "exit 5\r");

        Assert.Equal(5, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initial_size_and_resize_apply_to_the_console()
    {
        using var process = Start(Plan(["/d", "/q"]), new TerminalSize(101, 31));
        var output = new OutputCollector(process);

        Type(process, "mode con\r");
        await output.WaitForAsync("101");

        process.Resize(new TerminalSize(87, 23));
        Type(process, "cls\rmode con\r");
        await output.WaitForAsync("87");
        Type(process, "exit\r");
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        process.Resize(new TerminalSize(80, 24)); // after exit: ignored
    }

    [Fact]
    public async Task Terminate_ends_grandchildren_and_the_output()
    {
        using var process = Start(Plan(["/d", "/c", "ping -n 60 127.0.0.1 >nul"]));
        var output = new OutputCollector(process);
        var members = await WaitForMembersAsync(process, 2);

        process.Terminate();

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await output.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AssertAllExitedAsync(members);
    }

    [Fact]
    public async Task Dispose_ends_the_tree_without_a_reader()
    {
        var process = Start(Plan(["/d", "/c", "ping -n 60 127.0.0.1"]));
        var members = await WaitForMembersAsync(process, 2);

        // Nobody reads the output: disposing must still return promptly.
        await Task.Run(process.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        process.Dispose();

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await AssertAllExitedAsync(members);
        process.Resize(new TerminalSize(80, 24));
    }

    [Fact]
    public async Task Terminate_then_dispose_returns_while_a_full_output_pipe_is_unread()
    {
        // The console blocks flushing into a full pipe; closing it after the exit must not hang Dispose.
        var process = Start(Plan(["/d", "/c", "for /l %i in (1,1,200000) do @echo dotdeck-flood-line-with-some-padding-text"]));
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        process.Terminate();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await Task.Run(process.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Default_size_starts_as_one_cell()
    {
        Assert.Equal((1, 1), (default(TerminalSize).Columns, default(TerminalSize).Rows));
        using var process = Start(Plan(["/d", "/c", "exit 0"]), default(TerminalSize));
        var output = new OutputCollector(process);
        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exit_and_dispose_may_race()
    {
        for (var i = 0; i < 10; i++)
        {
            var process = Start(Plan(["/d", "/c", "exit 0"]));
            var output = new OutputCollector(process);
            await Task.Delay(i * 5, TestContext.Current.CancellationToken);
            await Task.Run(process.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await output.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Standard_input_text_and_relative_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Start(Plan(["/d", "/c", "exit"], stdin: "text")));
        Assert.Throws<ArgumentException>(() => new JobProcessLauncher().Start(Plan(["/d"]) with { Executable = "cmd.exe" }, TerminalSize.Default));
    }

    [Fact]
    public void Size_is_clamped()
    {
        Assert.Equal((1, 1), (new TerminalSize(0, -5).Columns, new TerminalSize(0, -5).Rows));
        Assert.Equal(TerminalSize.MaxCells, new TerminalSize(100_000, 10).Columns);
    }

    private static async Task<IReadOnlyList<int>> WaitForMembersAsync(ITerminalProcess process, int count)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var ids = process.JobProcessIds();
            if (ids.Count >= count)
            {
                return ids;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the child did not start its own child in time");
        return [];
    }

    private static async Task AssertAllExitedAsync(IReadOnlyList<int> ids)
    {
        var watch = Stopwatch.StartNew();
        while (ids.Any(IsRunning))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "still running: " + string.Join(", ", ids.Where(IsRunning)));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        static bool IsRunning(int id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                return !p.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
