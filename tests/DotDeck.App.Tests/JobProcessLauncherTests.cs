using System.Diagnostics;
using System.IO;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Launch;

namespace DotDeck.App.Tests;

/// <summary>Real processes in a real Job: pipes, environment, working directory, exit codes and tree termination.</summary>
public sealed class JobProcessLauncherTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string FindStr = Path.Combine(Environment.SystemDirectory, "findstr.exe");
    private readonly string _cwd = Path.Combine(Path.GetTempPath(), "dotdeck-job " + Guid.NewGuid().ToString("N"));

    public JobProcessLauncherTests() => Directory.CreateDirectory(_cwd);

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

    private CliLaunchPlan Plan(string exe, string[] args, string? stdin = null, Dictionary<string, string>? env = null) => new()
    {
        Executable = exe,
        Arguments = args,
        WorkingDirectory = _cwd,
        StandardInput = stdin,
        Environment = env ?? [],
    };

    private static async Task<(int Exit, string Stdout)> RunAsync(CliLaunchPlan plan)
    {
        using var process = new JobProcessLauncher().Start(plan);
        using var reader = new StreamReader(process.StandardOutput);
        var stdout = reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return (exit, await stdout.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stdin_is_delivered_and_stdout_read()
    {
        var (exit, stdout) = await RunAsync(Plan(FindStr, ["^"], stdin: "first line\r\nsecond line\r\n"));

        Assert.Equal(0, exit);
        Assert.Equal("first line\r\nsecond line\r\n", stdout);
    }

    [Fact]
    public async Task Environment_additions_and_working_directory_apply()
    {
        var (_, stdout) = await RunAsync(Plan(Cmd, ["/d", "/c", "echo [%DOTDECK_JOB_TEST%] & cd"], env: new() { ["DOTDECK_JOB_TEST"] = "a value" }));

        // cmd echoes in the OEM code page, so the value is ASCII here.
        Assert.Contains("[a value]", stdout, StringComparison.Ordinal);
        Assert.Contains(_cwd, stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exit_code_is_reported()
    {
        var (exit, _) = await RunAsync(Plan(Cmd, ["/d", "/c", "exit 7"]));

        Assert.Equal(7, exit);
    }

    [Fact]
    public async Task Terminate_ends_grandchildren()
    {
        using var process = new JobProcessLauncher().Start(Plan(Cmd, ["/d", "/c", "ping -n 60 127.0.0.1 >nul"]));
        var members = await WaitForMembersAsync(process, 2);

        process.Terminate();

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await AssertAllExitedAsync(members);
    }

    [Fact]
    public async Task Disposing_closes_the_job_and_ends_the_tree()
    {
        var process = new JobProcessLauncher().Start(Plan(Cmd, ["/d", "/c", "ping -n 60 127.0.0.1 >nul"]));
        var members = await WaitForMembersAsync(process, 2);

        process.Dispose();

        await AssertAllExitedAsync(members);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_completes_exited()
    {
        var process = new JobProcessLauncher().Start(Plan(Cmd, ["/d", "/c", "ping -n 60 127.0.0.1 >nul"]));

        process.Dispose();
        process.Dispose();

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispose_right_after_exit_does_not_race_the_exit_callback()
    {
        for (var i = 0; i < 20; i++)
        {
            var process = new JobProcessLauncher().Start(Plan(Cmd, ["/d", "/c", "exit 0"]));
            await Task.Delay(i % 5, TestContext.Current.CancellationToken);
            process.Dispose();
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Terminate_and_dispose_may_race_or_follow_each_other()
    {
        for (var i = 0; i < 20; i++)
        {
            var process = new JobProcessLauncher().Start(Plan(Cmd, ["/d", "/c", "ping -n 60 127.0.0.1 >nul"]));
            var stop = Task.Run(() => process.Terminate(), TestContext.Current.CancellationToken);
            var dispose = Task.Run(process.Dispose, TestContext.Current.CancellationToken);
            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            process.Terminate();
            Assert.Empty(process.JobProcessIds());
        }
    }

    [Fact]
    public void Relative_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new JobProcessLauncher().Start(Plan("cmd.exe", [])));
    }

    [Fact]
    public void Environment_block_is_sorted_and_terminated()
    {
        var block = JobProcessLauncher.BuildEnvironmentBlock(new Dictionary<string, string> { ["ZZZ_DOTDECK"] = "1", ["aaa_dotdeck"] = "2" });

        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
        var keys = block.TrimEnd('\0').Split('\0').Select(e => e[..e.IndexOf('=', 1)]).ToList();
        Assert.Equal(keys.Order(StringComparer.OrdinalIgnoreCase), keys);
        Assert.Throws<ArgumentException>(() => JobProcessLauncher.BuildEnvironmentBlock(new Dictionary<string, string> { ["A=B"] = "x" }));
    }

    private static async Task<IReadOnlyList<int>> WaitForMembersAsync(DotDeck.Core.AiCli.Sessions.ICliProcess process, int count)
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
        // Looking a process up needs no access rights (conhost in the job cannot be opened for waiting).
        var watch = Stopwatch.StartNew();
        while (ids.Any(IsRunning))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "still running: " + string.Join(", ", ids.Where(IsRunning).Select(i => { using var p = Process.GetProcessById(i); return $"{i} {p.ProcessName}"; })));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        static bool IsRunning(int id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
