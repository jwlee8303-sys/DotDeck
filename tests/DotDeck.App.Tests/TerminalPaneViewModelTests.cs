using System.Diagnostics;
using System.IO;
using System.Text;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.App.ViewModels.Tools;
using DotDeck.Core.AiCli.Launch;
using DotDeck.Core.AiCli.Sessions;

namespace DotDeck.App.Tests;

/// <summary>A terminal tab over a real pseudo console (cmd.exe): typed input, output, exit, copy and close requests.</summary>
public sealed class TerminalPaneViewModelTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private readonly string _cwd = Path.Combine(Path.GetTempPath(), "dotdeck-termpane " + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copied = [];

    public TerminalPaneViewModelTests() => Directory.CreateDirectory(_cwd);

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

    private TerminalPaneViewModel Pane(params string[] args)
    {
        var size = new TerminalSize(100, 30);
        var process = new JobProcessLauncher().Start(new CliLaunchPlan { Executable = Cmd, Arguments = args, WorkingDirectory = _cwd }, size);
        return new TerminalPaneViewModel("t1", "PowerShell", new TerminalConnection(process, size), _copied.Add, ui: null);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "condition not reached");
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Typed_input_round_trips_and_exit_marks_the_tab_ended()
    {
        var pane = Pane("/d", "/q");
        using var _ = pane.Connection;
        var output = new StringBuilder();
        var ended = 0;
        pane.Ended += (_, _) => ended++;
        pane.Connection.Attach(t => { lock (output) output.Append(t); });

        Assert.Equal("tool.terminal:t1", pane.ContentId);
        Assert.True(pane.IsRunning);

        pane.Connection.Send("echo 터미널-왕복\r");
        await Eventually(() => { lock (output) return output.ToString().Contains("터미널-왕복", StringComparison.Ordinal); });
        pane.Connection.Send("exit 4\r");

        await Eventually(() => !pane.IsRunning);
        Assert.Equal(4, pane.ExitCode);
        Assert.Equal("PowerShell (종료됨)", pane.Title);
        Assert.Contains("코드 4", pane.EndNotice, StringComparison.Ordinal);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void Copy_ignores_empty_selection_and_hiding_requests_close()
    {
        var pane = Pane("/d", "/q");
        using var _ = pane.Connection;
        var requests = 0;
        pane.CloseRequested += (_, _) => requests++;

        pane.Copy(string.Empty);
        pane.Copy("선택한 글자");
        pane.IsVisible = false;

        Assert.Equal(["선택한 글자"], _copied);
        Assert.Equal(1, requests);
    }
}
