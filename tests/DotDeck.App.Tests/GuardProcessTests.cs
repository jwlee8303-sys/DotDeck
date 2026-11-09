using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DotDeck.Core.AiCli.Scope;

namespace DotDeck.App.Tests;

/// <summary>
/// Runs the real DotDeck.exe in guard mode with redirected pipes, the way Claude Code runs an exec-form hook. Verifies
/// that the WinExe (GUI subsystem) process reads stdin and writes stdout without starting WPF.
/// </summary>
public sealed class GuardProcessTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dotdeck-guardproc-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _scope;

    public GuardProcessTests()
    {
        _root = Path.Combine(_temp, "proj");
        Directory.CreateDirectory(_root);
        _scope = Path.Combine(_temp, "session", "scope.json");
        ScopeFile.Write(_scope, new ScopeDocument { SessionId = "s", Roots = [new ScopeFileRoot { Path = _root }] });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string GuardExe => Path.Combine(AppContext.BaseDirectory, "DotDeck.exe");

    private (int Exit, string Stdout, TimeSpan Elapsed) RunGuard(string input, params string[] extraArgs)
    {
        var info = new ProcessStartInfo(GuardExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--dotdeck-guard");
        foreach (var arg in extraArgs.Length == 0 ? [_scope] : extraArgs)
        {
            info.ArgumentList.Add(arg);
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        // Read both pipes concurrently and bound the whole wait, so a hung guard fails the test instead of blocking it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("guard did not exit within 30 s");
        }

        Assert.True(Task.WaitAll([stdoutTask, stderrTask], 10_000), "guard pipes did not close");
        var stdout = stdoutTask.Result;
        watch.Stop();
        return (process.ExitCode, stdout, watch.Elapsed);
    }

    private string Input(string file) => new JsonObject
    {
        ["tool_name"] = "Edit",
        ["cwd"] = _root,
        ["tool_input"] = new JsonObject { ["file_path"] = file },
    }.ToJsonString();

    [Fact]
    public void Guard_process_denies_outside_path_over_pipes()
    {
        var (exit, stdout, _) = RunGuard(Input(@"C:\Windows\win.ini"));

        Assert.Equal(0, exit);
        Assert.Equal("deny", (string?)JsonNode.Parse(stdout)!["hookSpecificOutput"]!["permissionDecision"]);
    }

    [Fact]
    public void Guard_uses_explicit_backup_exclusions_but_keeps_other_file_backups()
    {
        var runtime = Path.Combine(_root, "state.json");
        var user = Path.Combine(_root, "test.md");
        File.WriteAllText(runtime, "fake runtime record");
        File.WriteAllText(user, "fake user document");
        var exclusionsFile = Path.Combine(_temp, "exclusions.json");
        var exclusions = new DotDeck.Core.Settings.PathExclusionStore(DotDeck.Core.Files.PhysicalFileStore.Instance, exclusionsFile);
        exclusions.Add(DotDeck.Core.Settings.ExclusionKind.AutomaticBackup, runtime);
        ScopeFile.Write(_scope, new ScopeDocument
        {
            SessionId = "s",
            Roots = [new ScopeFileRoot { Path = _root }],
            ExclusionsFile = exclusionsFile,
        });
        Assert.Equal(0, RunGuard(Input(runtime)).Exit);
        var backups = Path.Combine(Path.GetDirectoryName(_scope)!, "backups");
        Assert.False(Directory.Exists(backups) && Directory.EnumerateFiles(backups, "manifest.json", SearchOption.AllDirectories).Any());
        Assert.Equal(0, RunGuard(Input(user)).Exit);
        Assert.Single(Directory.EnumerateFiles(backups, "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Guard_process_is_silent_inside_scope()
    {
        var (exit, stdout, _) = RunGuard(Input(Path.Combine(_root, "새 파일.md")));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Guard_process_fails_closed_without_scope()
    {
        var (exit, stdout, _) = RunGuard(Input(Path.Combine(_root, "a.md")), Path.Combine(_temp, "missing.json"));

        Assert.Equal(2, exit);
        Assert.Contains("deny", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_process_latency_is_reported()
    {
        // Warm-up, then measure. Debug framework-dependent build; the single-file publish is measured separately.
        RunGuard(Input(Path.Combine(_root, "a.md")));
        var samples = Enumerable.Range(0, 5).Select(_ => RunGuard(Input(Path.Combine(_root, "a.md"))).Elapsed.TotalMilliseconds).Order().ToList();

        TestContext.Current.SendDiagnosticMessage($"guard p50 {samples[2]:F0} ms (min {samples[0]:F0}, max {samples[^1]:F0})");
        Assert.True(samples[2] < 5_000, $"guard took {samples[2]:F0} ms");
    }
}
