using DotDeck.Core.AiCli.Launch;

namespace DotDeck.Core.AiCli.Sessions;

/// <summary>Size of a pseudo console in character cells.</summary>
public readonly record struct TerminalSize
{
    /// <summary>Largest accepted dimension (a COORD field is a 16-bit signed value).</summary>
    public const int MaxCells = 4000;

    private readonly int _columns;
    private readonly int _rows;

    public TerminalSize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
    }

    // Clamped on read so default(TerminalSize) is a valid 1x1 size too.
    public int Columns => Math.Clamp(_columns, 1, MaxCells);

    public int Rows => Math.Clamp(_rows, 1, MaxCells);

    public static TerminalSize Default { get; } = new(120, 30);
}

/// <summary>
/// A process tree attached to a pseudo console (ConPTY), all in one Windows Job (kill on close, no breakaway).
/// <see cref="Output"/> carries the console's VT byte stream (UTF-8); bytes written to <see cref="Input"/> are typed
/// into the console (keys as VT sequences, Ctrl+C as 0x03).
/// </summary>
public interface ITerminalProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>
    /// The console's output. It ends after the main process has exited and the console has been closed, so a reader
    /// sees everything the process printed before the end of stream.
    /// </summary>
    Stream Output { get; }

    /// <summary>Console input. Writes can block while the console is busy; never write from the UI thread.</summary>
    Stream Input { get; }

    /// <summary>Completes with the exit code when the main process ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Changes the console size; ignored after the console has been closed.</summary>
    void Resize(TerminalSize size);

    /// <summary>Ids of all processes currently in the job (diagnostics and tests).</summary>
    IReadOnlyList<int> JobProcessIds();

    /// <summary>Ends every process in the job immediately.</summary>
    void Terminate(int exitCode = 1);
}

/// <summary>Starts a <see cref="CliLaunchPlan"/> in a new pseudo console (interactive sessions and terminals).</summary>
public interface ITerminalLauncher
{
    /// <summary>
    /// Starts the process with the given console size. <see cref="CliLaunchPlan.StandardInput"/> must be null: input
    /// comes from the terminal.
    /// </summary>
    ITerminalProcess Start(CliLaunchPlan plan, TerminalSize size);
}
