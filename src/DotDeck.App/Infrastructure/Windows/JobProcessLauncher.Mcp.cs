using System.IO;
using DotDeck.Core.AiCli.ContextBudget;
using DotDeck.Core.AiCli.Launch;
using Microsoft.Win32.SafeHandles;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// MCP measurement runs ("컨텍스트" tab): the server's own command line, its configured env added to the inherited
/// environment, stdin and stdout pipes kept open, stderr read and discarded, no window. The process tree lives in a
/// kill-on-close job, so disposing ends every process the server started.
/// </summary>
public sealed partial class JobProcessLauncher : IMcpProcessHost
{
    public IMcpProcess Start(string executable, string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(environment);
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("실행 파일과 작업 폴더는 절대 경로여야 합니다.");
        }

        var plan = new CliLaunchPlan { Executable = executable, Arguments = [], WorkingDirectory = workingDirectory, Environment = environment };
        var job = CreateKillOnCloseJob();
        SafeFileHandle? stdinParent = null, stdinChild = null, stdoutParent = null, stdoutChild = null, stderrParent = null, stderrChild = null;
        try
        {
            CreatePipePair(childReads: true, inheritable: true, out stdinParent, out stdinChild);
            CreatePipePair(childReads: false, inheritable: true, out stdoutParent, out stdoutChild);
            CreatePipePair(childReads: false, inheritable: true, out stderrParent, out stderrChild);

            var info = CreateInJob(plan, job, [stdinChild, stdoutChild, stderrChild], console: null, rawCommandLine: commandLine);
            var process = new McpProcess(job, info, stdinParent, stdoutParent, stderrParent);
            job = null;
            stdinParent = null;
            stdoutParent = null;
            stderrParent = null;
            return process;
        }
        finally
        {
            stdinChild?.Dispose();
            stdoutChild?.Dispose();
            stderrChild?.Dispose();
            stdinParent?.Dispose();
            stdoutParent?.Dispose();
            stderrParent?.Dispose();
            job?.Dispose();
        }
    }

    private sealed class McpProcess : JobProcessBase, IMcpProcess
    {
        private readonly FileStream _error;

        public McpProcess(SafeJobHandle job, PROCESS_INFORMATION info, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
            : base(job, info)
        {
            Input = new FileStream(stdin, FileAccess.Write, 1, isAsync: false);
            Output = new FileStream(stdout, FileAccess.Read, 4096, isAsync: false);
            _error = new FileStream(stderr, FileAccess.Read, 4096, isAsync: false);
            _ = Task.Run(DrainError); // a full stderr pipe would block the server
            StartWaiting();
        }

        public Stream Input { get; }

        public Stream Output { get; }

        private void DrainError()
        {
            var buffer = new byte[4096];
            try
            {
                while (_error.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        protected override void DisposeStreams()
        {
            Input.Dispose();
            Output.Dispose();
            _error.Dispose();
        }
    }
}
