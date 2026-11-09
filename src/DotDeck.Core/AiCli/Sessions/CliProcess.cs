using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.Sessions;

/// <summary>
/// A started CLI process tree. The whole tree lives in one Windows Job (kill on close, no breakaway), so terminating or
/// disposing it also ends grandchildren (node, shells, tools started by the CLI).
/// </summary>
public interface ICliProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>Raw stdout bytes (UTF-8 for both CLIs).</summary>
    Stream StandardOutput { get; }

    Stream StandardError { get; }

    /// <summary>Completes with the exit code when the main process ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Ids of all processes currently in the job (diagnostics and tests).</summary>
    IReadOnlyList<int> JobProcessIds();

    /// <summary>Ends every process in the job immediately.</summary>
    void Terminate(int exitCode = 1);
}

/// <summary>Starts a <see cref="CliLaunchPlan"/> with redirected pipes (non-interactive runs; terminals use <see cref="ITerminalLauncher"/>).</summary>
public interface ICliProcessLauncher
{
    /// <summary>Starts the process; <see cref="CliLaunchPlan.StandardInput"/> is written as UTF-8 and stdin is closed.</summary>
    ICliProcess Start(CliLaunchPlan plan);
}
