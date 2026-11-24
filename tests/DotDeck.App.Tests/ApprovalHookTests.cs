using System.IO;
using System.Text;
using DotDeck.App.Infrastructure.Windows;
using DotDeck.Core.AiCli.Approvals;
using DotDeck.Core.AiCli.Mcp;

namespace DotDeck.App.Tests;

public sealed class ApprovalHookTests
{
    [Fact]
    public async Task UnreachableApprovalHostReturnsSuccessWithNoOutput()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), id));
        try
        {
            using var input = HookInput();
            using var output = new MemoryStream();

            var exitCode = await NamedPipeApprovalHost.RunRequestHookAsync(
                [ClaudeApprovalHook.RequestSwitch, directory.FullName], input, output)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(0L, output.Length);
        }
        finally
        {
            directory.Delete();
        }
    }

    [Fact]
    public async Task InvalidHookInputReturnsSuccessWithNoOutput()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), id));
        try
        {
            using var input = new MemoryStream([0xff]);
            using var output = new MemoryStream();

            var exitCode = await NamedPipeApprovalHost.RunRequestHookAsync(
                [ClaudeApprovalHook.RequestSwitch, directory.FullName], input, output);

            Assert.Equal(0, exitCode);
            Assert.Equal(0L, output.Length);
        }
        finally
        {
            directory.Delete();
        }
    }

    [Fact]
    public async Task HostShutdownWhileWaitingReturnsSuccessWithNoOutput()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), id));
        try
        {
            var sink = new WaitingSink();
            using var host = new NamedPipeApprovalHost(directory.FullName, id, sink);
            using var input = HookInput();
            using var output = new MemoryStream();
            var cancellation = TestContext.Current.CancellationToken;

            var hook = NamedPipeApprovalHost.RunRequestHookAsync(
                [ClaudeApprovalHook.RequestSwitch, directory.FullName], input, output);
            await sink.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            host.Dispose();

            Assert.Equal(0, await hook.WaitAsync(TimeSpan.FromSeconds(10), cancellation));
            Assert.Equal(0L, output.Length);
        }
        finally
        {
            File.Delete(Path.Combine(directory.FullName, ClaudeApprovalHook.KeyFile));
            directory.Delete();
        }
    }

    [Fact]
    public async Task HookConnectionWritesOnlyOneDecision()
    {
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var connection = new ApprovalHookConnection(writer);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.True(await connection.SendAsync(ApprovalDecision.Deny, cancellation));
        Assert.False(await connection.SendAsync(ApprovalDecision.Allow, cancellation));
        connection.Release();

        output.Position = 0;
        using var reader = new StreamReader(output);
        Assert.Equal(ClaudeApprovalHook.BehaviorDeny,
            ClaudeApprovalHook.ReadDecision(await JsonRpcFraming.ReadAsync(reader, cancellation)));
        Assert.Null(await JsonRpcFraming.ReadAsync(reader, cancellation));
    }

    private static MemoryStream HookInput() =>
        new(Encoding.UTF8.GetBytes("""{"tool_name":"Bash","tool_input":{"command":"preview sample.txt"}}"""));

    private sealed class WaitingSink : IApprovalHookSink
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnRequest(ClaudeApprovalHook.RequestMessage message, ApprovalHookConnection connection) =>
            Received.TrySetResult();

        public void OnToolRan(string match)
        {
        }
    }
}
